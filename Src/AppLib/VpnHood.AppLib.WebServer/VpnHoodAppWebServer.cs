﻿using System.IO.Compression;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmbedIO;
using EmbedIO.Files;
using EmbedIO.WebApi;
using Swan.Logging;
using VpnHood.AppLib.WebServer.Controllers;
using VpnHood.Core.Toolkit.Utils;

namespace VpnHood.AppLib.WebServer;

public class VpnHoodAppWebServer : Singleton<VpnHoodAppWebServer>, IDisposable
{
    private readonly Stream _spaZipStream;
    private string? _indexHtml;
    private EmbedIO.WebServer? _server;
    private string? _spaHash;
    private readonly bool _listenOnAllIps;

    private VpnHoodAppWebServer(WebServerOptions options)
    {
        _spaZipStream = options.SpaZipStream;
        Url = options.Url ??
              new Uri($"http://{VhUtils.GetFreeTcpEndPoint(IPAddress.Loopback, options.DefaultPort ?? 9090)}");
        _listenOnAllIps = options.ListenOnAllIps;
    }

    public Uri Url { get; }

    public string SpaHash =>
        _spaHash ?? throw new InvalidOperationException($"{nameof(SpaHash)} is not initialized");

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            Stop();

        base.Dispose(disposing);
    }

    public static VpnHoodAppWebServer Init(WebServerOptions options)
    {
        var ret = new VpnHoodAppWebServer(options);
        ret.Start();
        return ret;
    }

    public void Start()
    {
        if (_server != null)
            return;

        _server = CreateWebServer();
        try {
            Logger.UnregisterLogger<ConsoleLogger>();
        }
        catch {
            // ignored
        }

        _server.RunAsync();
    }

    public void Stop()
    {
        _server?.Dispose();
        _server = null;
    }

    private string? _spaPath;

    private string GetSpaPath()
    {
        if (_spaPath != null)
            return _spaPath; // do not extract in same instance

        using var memZipStream = new MemoryStream();
        _spaZipStream.CopyTo(memZipStream);

        // extract the resource
        memZipStream.Seek(0, SeekOrigin.Begin);
        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(memZipStream);
        _spaHash = BitConverter.ToString(hash).Replace("-", "");

        var spaFolderPath = Path.Combine(VpnHoodApp.Instance.StorageFolderPath, "Temp", "SPA");
        var spaPath = Path.Combine(spaFolderPath, _spaHash);
        var htmlPath = Path.Combine(spaPath, "index.html");
        if (!File.Exists(htmlPath)) {
            try {
                Directory.Delete(spaFolderPath, true);
            }
            catch {
                // ignored
            }

            memZipStream.Seek(0, SeekOrigin.Begin);
            using var zipArchive = new ZipArchive(memZipStream);
            zipArchive.ExtractToDirectory(spaPath, true);
        }

        _spaZipStream.Dispose();
        _spaPath = spaPath;
        return spaPath;
    }

    private EmbedIO.WebServer CreateWebServer()
    {
        // read index.html for fallback
        var spaPath = GetSpaPath();
        _indexHtml = File.ReadAllText(Path.Combine(spaPath, "index.html"));
        var urlPrefixes = new List<string> { Url.AbsoluteUri };
        if (_listenOnAllIps)
            urlPrefixes.AddRange(GetAllPublicIp4().Select(x => $"http://{x}:{Url.Port}"));

        // cors
        var cors = VpnHoodApp.Instance.Features.IsDebugMode
            ? "*"
            : "https://localhost:8080, http://localhost:8080, https://localhost:8081, http://localhost:8081, http://localhost:30080";

        // create the server
        var server = new EmbedIO.WebServer(o => o
                .WithUrlPrefixes(urlPrefixes.Distinct())
                .WithMode(HttpListenerMode.EmbedIO))
            .WithCors(cors) // must be first
            .WithWebApi("/api/app", ResponseSerializerCallback, c => c
                .WithController<AppController>()
                .HandleUnhandledException(ExceptionHandler.DataResponseForException))
            .WithWebApi("/api/client-profiles", ResponseSerializerCallback, c => c
                .WithController<ClientProfileController>()
                .HandleUnhandledException(ExceptionHandler.DataResponseForException))
            .WithWebApi("/api/account", ResponseSerializerCallback, c => c
                .WithController<AccountController>()
                .HandleUnhandledException(ExceptionHandler.DataResponseForException))
            .WithWebApi("/api/billing", ResponseSerializerCallback, c => c
                .WithController<BillingController>()
                .HandleUnhandledException(ExceptionHandler.DataResponseForException))
            .WithStaticFolder("/", spaPath, true, c => {
                c.WithContentCaching(!VpnHoodApp.Instance.Features.IsDebugMode);
                c.HandleMappingFailed(HandleMappingFailed);
            })
            .HandleHttpException(ExceptionHandler.DataResponseForHttpException);

        return server;
    }

    private static async Task ResponseSerializerCallback(IHttpContext context, object? data)
    {
        if (context.IsHandled) 
            return;

        if (data is null) {
            context.Response.StatusCode = (int)HttpStatusCode.NoContent;
            return;
        }

        context.Response.StatusCode = (int)HttpStatusCode.OK;
        context.Response.ContentType = MimeType.Json;
        await using var text = context.OpenResponseText(new UTF8Encoding(false));
        await text.WriteAsync(JsonSerializer.Serialize(data,
                new JsonSerializerOptions {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                }))
            .Vhc();
    }

    // manage SPA fallback
    private Task HandleMappingFailed(IHttpContext context, MappedResourceInfo? info)
    {
        if (context.IsHandled) return Task.CompletedTask;
        if (_indexHtml == null) throw new InvalidOperationException($"{nameof(_indexHtml)} is not initialized");

        if (string.IsNullOrEmpty(Path.GetExtension(context.Request.Url.LocalPath)))
            return context.SendStringAsync(_indexHtml, "text/html", Encoding.UTF8);

        throw HttpException.NotFound();
    }

    private static IEnumerable<IPAddress> GetAllPublicIp4()
    {
        var networkInterfaces = NetworkInterface.GetAllNetworkInterfaces()
            .Where(x =>
                x.OperationalStatus is OperationalStatus.Up &&
                x.Supports(NetworkInterfaceComponent.IPv4) &
                x.NetworkInterfaceType is not NetworkInterfaceType.Loopback);

        var ipAddresses = new List<IPAddress>();
        foreach (var networkInterface in networkInterfaces) {
            var ipProperties = networkInterface.GetIPProperties();
            var uniCastAddresses = ipProperties.UnicastAddresses;
            var ips = uniCastAddresses
                .Where(x => x.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(x => x.Address);

            ipAddresses.AddRange(ips);
        }

        return ipAddresses.ToArray();
    }
}
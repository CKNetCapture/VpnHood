﻿using VpnHood.AppLib.Abstractions;

namespace VpnHood.AppLib.Services.Ads;

public class AppAdProviderItem
{
    public string Name => ProviderName ?? AdProvider.NetworkName;
    public string? ProviderName { get; init; }
    public required IAppAdProvider AdProvider { get; init; }
    public bool CanShowOverVpn { get; init; }
    public string[] IncludeCountryCodes { get; init; } = [];
    public string[] ExcludeCountryCodes { get; init; } = [];
}
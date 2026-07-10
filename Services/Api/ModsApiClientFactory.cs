using UnturnedModLoader.I18n;
using UnturnedModLoader.Models;
using UnturnedModLoader.Services;

namespace UnturnedModLoader.Services.Api;

public static class ModsApiClientFactory
{
    public static IModsApiClient Create(AppSettings settings) =>
        new HttpModsApiClient(ResolveBaseUrl(settings));

    public static string ResolveBaseUrl(AppSettings settings) =>
        settings.ApiProvider switch
        {
            Models.ApiProvider.Cloud when !string.IsNullOrWhiteSpace(settings.CloudApiBaseUrl)
                => settings.CloudApiBaseUrl.TrimEnd('/'),
            Models.ApiProvider.Cloud
                => throw new InvalidOperationException(L.Get(I18n.ApiMessages.CloudNotConfigured)),
            _ => string.IsNullOrWhiteSpace(settings.LocalApiBaseUrl)
                ? "http://localhost:3000"
                : settings.LocalApiBaseUrl.TrimEnd('/'),
        };
}
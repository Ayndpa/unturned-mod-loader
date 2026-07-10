using UnturnedModLoader.Models;

namespace UnturnedModLoader.Services.Api;

public static class ModsApiClientFactory
{
    public static IModsApiClient Create(AppSettings settings) =>
        new HttpModsApiClient(ResolveBaseUrl(settings));

    public static string ResolveBaseUrl(AppSettings settings) =>
        settings.ApiProvider switch
        {
            ApiProvider.Cloud when !string.IsNullOrWhiteSpace(settings.CloudApiBaseUrl)
                => settings.CloudApiBaseUrl.TrimEnd('/'),
            ApiProvider.Cloud
                => throw new InvalidOperationException("云端 API 地址未配置。"),
            _ => string.IsNullOrWhiteSpace(settings.LocalApiBaseUrl)
                ? "http://localhost:3000"
                : settings.LocalApiBaseUrl.TrimEnd('/'),
        };
}
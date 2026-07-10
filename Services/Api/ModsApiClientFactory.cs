using UnturnedModLoader.Models;

namespace UnturnedModLoader.Services.Api;

public static class ModsApiClientFactory
{
    public const string DefaultBaseUrl = "https://unmod.online";

    public static IModsApiClient Create(AppSettings settings) =>
        new HttpModsApiClient(ResolveBaseUrl(settings));

    public static string ResolveBaseUrl(AppSettings settings) => DefaultBaseUrl;
}
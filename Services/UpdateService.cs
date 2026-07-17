using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace UnturnedModLoader.Services;

public sealed class UpdateService
{
    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    static UpdateService()
    {
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("UnturnedModLoader-Updater/1.0");
    }

    public sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string TagName,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("body")] string Body,
        [property: JsonPropertyName("html_url")] string HtmlUrl
    );

    public static async Task<GitHubRelease?> CheckForUpdatesAsync(string repo = "Ayndpa/unturned-mod-loader")
    {
        try
        {
            var url = $"https://api.github.com/repos/{repo}/releases/latest";
            var release = await _httpClient.GetFromJsonAsync<GitHubRelease>(url);
            return release;
        }
        catch
        {
            return null;
        }
    }

    public static Version GetCurrentVersion()
    {
        return typeof(UpdateService).Assembly.GetName().Version ?? new Version(1, 0, 0);
    }

    public static bool IsNewerVersion(string latestTag, out Version? latestVersion)
    {
        latestVersion = null;
        var cleanTag = latestTag.TrimStart('v', 'V');
        if (Version.TryParse(cleanTag, out var parsedVersion))
        {
            latestVersion = parsedVersion;
            var currentVersion = GetCurrentVersion();
            return CompareVersions(parsedVersion, currentVersion) > 0;
        }
        return false;
    }

    private static int CompareVersions(Version a, Version b)
    {
        var aMajor = a.Major;
        var aMinor = a.Minor;
        var aBuild = a.Build < 0 ? 0 : a.Build;
        var aRevision = a.Revision < 0 ? 0 : a.Revision;

        var bMajor = b.Major;
        var bMinor = b.Minor;
        var bBuild = b.Build < 0 ? 0 : b.Build;
        var bRevision = b.Revision < 0 ? 0 : b.Revision;

        if (aMajor != bMajor) return aMajor.CompareTo(bMajor);
        if (aMinor != bMinor) return aMinor.CompareTo(bMinor);
        if (aBuild != bBuild) return aBuild.CompareTo(bBuild);
        return aRevision.CompareTo(bRevision);
    }
}

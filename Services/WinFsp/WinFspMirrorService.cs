using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UnturnedModLoader.Services.WinFsp;

/// <summary>
/// WinFsp MSI download mirrors. Mirrors the 6 download sources used by the web
/// client's Mod Loader page (direct GitHub + 5 gh-proxy variants) so desktop and
/// web users see the same picker and the same fastest-mirror selection.
/// </summary>
public enum WinFspMirror
{
    Direct,
    GhProxyCom,
    GhProxyOrg,
    V4GhProxyOrg,
    V6GhProxyOrg,
    CdnGhProxyOrg,
}

/// <summary>Result of probing one mirror: latency to first response byte, or a timeout.</summary>
public sealed record MirrorProbe(WinFspMirror Mirror, int LatencyMs, bool TimedOut);

/// <summary>
/// Resolves the WinFsp MSI asset URL, probes every mirror concurrently, and picks the
/// fastest. Probe mirrors the browser's <c>no-cors</c> ping: a short GET that is cancelled
/// as soon as response headers arrive, so we measure latency without downloading the MSI.
/// </summary>
public static class WinFspMirrorService
{
    private const int ProbeTimeoutMs = 4000;

    /// <summary>
    /// Must match the <c>winfsp.net</c> NuGet package version so the native MSI pairs with
    /// the managed assembly (CheckVersion compares major.minor of both).
    /// </summary>
    public const string RequiredNativeVersion = "2.2.26194";

    private const string ReleasesApi = "https://api.github.com/repos/winfsp/winfsp/releases?per_page=20";
    private const string FallbackMsiUrl =
        "https://github.com/winfsp/winfsp/releases/download/v2.2B3/winfsp-2.2.26194.msi";

    private static readonly Regex MsiNameRegex = new(
        @"^winfsp-(\d+\.\d+\.\d+)\.msi$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Mirror order matches the web client PROXY_OPTIONS.</summary>
    public static IReadOnlyList<WinFspMirror> All { get; } =
    [
        WinFspMirror.Direct,
        WinFspMirror.GhProxyCom,
        WinFspMirror.GhProxyOrg,
        WinFspMirror.V4GhProxyOrg,
        WinFspMirror.V6GhProxyOrg,
        WinFspMirror.CdnGhProxyOrg,
    ];

    private static readonly HttpClient ProbeClient = CreateProbeClient();
    private static readonly HttpClient ApiClient = new(new HttpClientHandler { AllowAutoRedirect = true })
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    /// <summary>URL prefix prepended to a raw GitHub URL for the given mirror (empty for direct).</summary>
    public static string GetPrefix(WinFspMirror mirror) => mirror switch
    {
        WinFspMirror.GhProxyCom => "https://gh-proxy.com/",
        WinFspMirror.GhProxyOrg => "https://gh-proxy.org/",
        WinFspMirror.V4GhProxyOrg => "https://v4.gh-proxy.org/",
        WinFspMirror.V6GhProxyOrg => "https://v6.gh-proxy.org/",
        WinFspMirror.CdnGhProxyOrg => "https://cdn.gh-proxy.org/",
        _ => "",
    };

    /// <summary>Apply a mirror's prefix to a raw GitHub release URL.</summary>
    public static string ApplyMirror(string rawGithubUrl, WinFspMirror mirror) =>
        GetPrefix(mirror) + rawGithubUrl;

    /// <summary>
    /// Resolves a winfsp-*.msi <c>browser_download_url</c> from GitHub releases (including
    /// pre-releases). Prefers an MSI whose version matches <see cref="RequiredNativeVersion"/>
    /// (the managed <c>winfsp.net</c> package), then the newest 2.x MSI overall. Falls back to
    /// a pinned URL if the API is unreachable.
    /// </summary>
    public static async Task<string> ResolveMsiAssetUrlAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, ReleasesApi);
            req.Headers.UserAgent.ParseAdd("UnturnedModLoader");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");

            using var resp = await ApiClient.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode)
            {
                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                if (TryPickMsiFromReleases(doc.RootElement, out var url) && !string.IsNullOrWhiteSpace(url))
                    return url;
            }
        }
        catch
        {
            // Fall through to pinned URL.
        }

        return FallbackMsiUrl;
    }

    /// <summary>
    /// Scan a GitHub <c>/releases</c> JSON array (stable + pre-release) and pick the best MSI.
    /// </summary>
    internal static bool TryPickMsiFromReleases(JsonElement releases, out string? url)
    {
        url = null;
        string? exactMatch = null;
        string? newestAny = null;
        Version? newestVer = null;

        if (releases.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var release in releases.EnumerateArray())
        {
            // Skip drafts; pre-releases are intentionally included so 2.2 betas are visible.
            if (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
                continue;

            if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var asset in assets.EnumerateArray())
            {
                if (!asset.TryGetProperty("name", out var nameProp))
                    continue;
                var name = nameProp.GetString();
                if (string.IsNullOrEmpty(name))
                    continue;
                if (name.Contains("debug", StringComparison.OrdinalIgnoreCase))
                    continue;

                var match = MsiNameRegex.Match(name);
                if (!match.Success)
                    continue;
                if (!asset.TryGetProperty("browser_download_url", out var urlProp))
                    continue;
                var assetUrl = urlProp.GetString();
                if (string.IsNullOrWhiteSpace(assetUrl))
                    continue;

                var verText = match.Groups[1].Value;
                if (string.Equals(verText, RequiredNativeVersion, StringComparison.OrdinalIgnoreCase))
                    exactMatch = assetUrl;

                if (Version.TryParse(verText, out var ver))
                {
                    if (newestVer is null || ver > newestVer)
                    {
                        newestVer = ver;
                        newestAny = assetUrl;
                    }
                }
            }
        }

        url = exactMatch ?? newestAny;
        return url is not null;
    }

    /// <summary>
    /// Probes every mirror concurrently against <paramref name="rawGithubUrl"/>. Each probe is a
    /// ranged GET cancelled as soon as headers arrive; latency is measured to that point. Mirrors
    /// that error or exceed the timeout come back as <see cref="MirrorProbe.TimedOut"/>.
    /// </summary>
    public static async Task<IReadOnlyList<MirrorProbe>> ProbeAsync(
        string rawGithubUrl,
        CancellationToken ct = default)
    {
        var tasks = All.Select(m => ProbeOneAsync(rawGithubUrl, m, ct));
        return await Task.WhenAll(tasks);
    }

    /// <summary>Picks the lowest-latency non-timed-out mirror; falls back to Direct if all failed.</summary>
    public static WinFspMirror PickFastest(IReadOnlyList<MirrorProbe> probes)
    {
        var best = probes.Where(p => !p.TimedOut)
            .MinBy(p => p.LatencyMs);
        return best?.Mirror ?? WinFspMirror.Direct;
    }

    private static async Task<MirrorProbe> ProbeOneAsync(
        string rawGithubUrl,
        WinFspMirror mirror,
        CancellationToken ct)
    {
        var url = ApplyMirror(rawGithubUrl, mirror);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ProbeTimeoutMs);

        var sw = Stopwatch.StartNew();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            // Ask for a single byte so the server sends headers (and starts a body we never read)
            // without us pulling the whole MSI. Mirrors that reject Range still return headers.
            req.Headers.Range = new RangeHeaderValue(0, 0);

            using var resp = await ProbeClient.SendAsync(
                req, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            sw.Stop();
            return new MirrorProbe(mirror, (int)sw.ElapsedMilliseconds, TimedOut: false);
        }
        catch
        {
            return new MirrorProbe(mirror, int.MaxValue, TimedOut: true);
        }
    }

    private static HttpClient CreateProbeClient()
    {
        // Follow redirects (gh-proxy 302s to a CDN) but never buffer a body.
        var handler = new HttpClientHandler { AllowAutoRedirect = true };
        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMilliseconds(ProbeTimeoutMs),
        };
    }
}

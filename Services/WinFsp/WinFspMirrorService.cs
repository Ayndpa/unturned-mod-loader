using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;

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
    private const string WinFspReleasesApi = "https://api.github.com/repos/winfsp/winfsp/releases/latest";
    private const string FallbackMsiUrl = "https://github.com/winfsp/winfsp/releases/download/v2.1/winfsp-2.1.25156.msi";

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
    /// Resolves the latest winfsp-*.msi <c>browser_download_url</c> from the GitHub releases
    /// API, falling back to a pinned URL if the API is unreachable. Returns a raw
    /// <c>github.com/...</c> URL (un-mirrored) so callers can probe every mirror against it.
    /// </summary>
    public static async Task<string> ResolveMsiAssetUrlAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, WinFspReleasesApi);
            req.Headers.UserAgent.ParseAdd("UnturnedModLoader");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");

            using var resp = await ApiClient.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode)
            {
                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                if (doc.RootElement.TryGetProperty("assets", out var assets))
                {
                    foreach (var asset in assets.EnumerateArray())
                    {
                        if (!asset.TryGetProperty("name", out var nameProp))
                            continue;
                        var name = nameProp.GetString();
                        if (string.IsNullOrEmpty(name))
                            continue;

                        // Match the script's filter: winfsp-<version>.msi, skip debug builds.
                        if (name.StartsWith("winfsp-", StringComparison.OrdinalIgnoreCase)
                            && name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)
                            && !name.Contains("debug", StringComparison.OrdinalIgnoreCase)
                            && asset.TryGetProperty("browser_download_url", out var urlProp))
                        {
                            var url = urlProp.GetString();
                            if (!string.IsNullOrWhiteSpace(url))
                                return url;
                        }
                    }
                }
            }
        }
        catch
        {
            // Fall through to pinned URL.
        }

        return FallbackMsiUrl;
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

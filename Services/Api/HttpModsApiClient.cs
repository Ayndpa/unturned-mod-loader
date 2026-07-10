using System.Net.Http.Json;
using System.Text.Json;
using UnturnedModLoader.Models.Api;

namespace UnturnedModLoader.Services.Api;

public sealed class HttpModsApiClient : IModsApiClient, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;

    public HttpModsApiClient(string baseUrl, HttpClient? httpClient = null)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        _http = httpClient ?? new HttpClient
        {
            BaseAddress = new Uri(BaseUrl + "/"),
            Timeout = TimeSpan.FromSeconds(15),
        };
    }

    public string BaseUrl { get; }

    public async Task<ModsListResult> GetModsAsync(
        ModsQuery query,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var allMods = new List<RemoteMod>();
            var page = 1;
            var totalPages = 1;
            var total = 0;

            while (page <= totalPages)
            {
                var requestUri = BuildRequestUri(query, page);
                using var response = await _http.GetAsync(requestUri, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(cancellationToken);
                    return new ModsListResult(
                        false,
                        [],
                        0,
                        ParseErrorMessage(body) ?? $"请求失败 (HTTP {(int)response.StatusCode})");
                }

                var payload = await response.Content
                    .ReadFromJsonAsync<ModsListResponse>(JsonOptions, cancellationToken);

                if (payload is null)
                    return new ModsListResult(false, [], 0, "服务端返回了无效数据。");

                allMods.AddRange(payload.Mods);
                total = payload.Total;
                totalPages = Math.Max(1, payload.Pages);
                page++;
            }

            return new ModsListResult(true, allMods, total, null);
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            return new ModsListResult(false, [], 0, "请求超时，请检查服务端是否已启动。");
        }
        catch (HttpRequestException ex)
        {
            return new ModsListResult(false, [], 0, $"无法连接 API：{ex.Message}");
        }
        catch (Exception ex)
        {
            return new ModsListResult(false, [], 0, $"加载模组失败：{ex.Message}");
        }
    }

    private static string BuildRequestUri(ModsQuery query, int page)
    {
        var parameters = new List<string> { $"page={page}" };

        if (!string.IsNullOrWhiteSpace(query.Category))
            parameters.Add($"category={Uri.EscapeDataString(query.Category)}");

        if (!string.IsNullOrWhiteSpace(query.Search))
            parameters.Add($"q={Uri.EscapeDataString(query.Search.Trim())}");

        if (!string.IsNullOrWhiteSpace(query.Sort))
            parameters.Add($"sort={Uri.EscapeDataString(query.Sort)}");

        return $"api/mods?{string.Join('&', parameters)}";
    }

    private static string? ParseErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error))
                return error.GetString();
        }
        catch
        {
            // Ignore malformed error payloads.
        }

        return null;
    }

    public void Dispose() => _http.Dispose();
}
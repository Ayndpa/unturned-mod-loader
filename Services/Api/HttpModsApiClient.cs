using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using UnturnedModLoader.I18n;
using UnturnedModLoader.Models.Api;
using UnturnedModLoader.Services;

namespace UnturnedModLoader.Services.Api;

public sealed partial class HttpModsApiClient : IModsApiClient, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly HttpClient _downloadHttp;

    public HttpModsApiClient(string baseUrl, HttpClient? httpClient = null, HttpClient? downloadHttpClient = null)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        _http = httpClient ?? new HttpClient
        {
            BaseAddress = new Uri(BaseUrl + "/"),
            Timeout = TimeSpan.FromSeconds(15),
        };
        _downloadHttp = downloadHttpClient ?? _http;
    }

    public HttpModsApiClient(HttpClient httpClient, HttpClient? downloadHttpClient = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        if (httpClient.BaseAddress is not Uri baseAddress)
            throw new ArgumentException("HttpClient.BaseAddress must be set.", nameof(httpClient));

        BaseUrl = baseAddress.AbsoluteUri.TrimEnd('/');
        _http = httpClient;
        _downloadHttp = downloadHttpClient ?? httpClient;
    }

    public string BaseUrl { get; }

    public async Task<CategoriesListResult> GetCategoriesAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http.GetAsync("api/categories", cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                return new CategoriesListResult(
                    false,
                    [],
                    ParseErrorMessage(body) ?? L.Get(I18n.ApiMessages.RequestFailed, (int)response.StatusCode));
            }

            var payload = await response.Content
                .ReadFromJsonAsync<CategoriesListResponse>(JsonOptions, cancellationToken);

            if (payload is null)
                return new CategoriesListResult(false, [], L.Get(I18n.ApiMessages.InvalidResponse));

            return new CategoriesListResult(true, payload.Categories, null);
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            return new CategoriesListResult(false, [], L.Get(I18n.ApiMessages.Timeout));
        }
        catch (HttpRequestException ex)
        {
            return new CategoriesListResult(false, [], L.Get(I18n.ApiMessages.CannotConnect, ex.Message));
        }
        catch (Exception ex)
        {
            return new CategoriesListResult(false, [], L.Get(I18n.ApiMessages.LoadCategoriesFailed, ex.Message));
        }
    }

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
                        ParseErrorMessage(body) ?? L.Get(I18n.ApiMessages.RequestFailed, (int)response.StatusCode));
                }

                var payload = await response.Content
                    .ReadFromJsonAsync<ModsListResponse>(JsonOptions, cancellationToken);

                if (payload is null)
                    return new ModsListResult(false, [], 0, L.Get(I18n.ApiMessages.InvalidResponse));

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
            return new ModsListResult(false, [], 0, L.Get(I18n.ApiMessages.Timeout));
        }
        catch (HttpRequestException ex)
        {
            return new ModsListResult(false, [], 0, L.Get(I18n.ApiMessages.CannotConnect, ex.Message));
        }
        catch (Exception ex)
        {
            return new ModsListResult(false, [], 0, L.Get(I18n.ApiMessages.LoadModsFailed, ex.Message));
        }
    }

    public async Task<ModDetailResult> GetModAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http.GetAsync($"api/mods/{id}", cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                return new ModDetailResult(
                    false,
                    null,
                    ParseErrorMessage(body) ?? L.Get(I18n.ApiMessages.RequestFailed, (int)response.StatusCode));
            }

            var payload = await response.Content
                .ReadFromJsonAsync<ModDetailResponse>(JsonOptions, cancellationToken);

            if (payload?.Mod is null)
                return new ModDetailResult(false, null, L.Get(I18n.ApiMessages.InvalidResponse));

            return new ModDetailResult(true, payload.Mod, null);
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            return new ModDetailResult(false, null, L.Get(I18n.ApiMessages.Timeout));
        }
        catch (HttpRequestException ex)
        {
            return new ModDetailResult(false, null, L.Get(I18n.ApiMessages.CannotConnect, ex.Message));
        }
        catch (Exception ex)
        {
            return new ModDetailResult(false, null, L.Get(I18n.ApiMessages.LoadModDetailFailed, ex.Message));
        }
    }

    public async Task<ModFileDownloadResult> DownloadModFileAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _downloadHttp.GetAsync($"api/mods/{id}/file", cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                return new ModFileDownloadResult(
                    false,
                    null,
                    null,
                    ParseErrorMessage(body) ?? L.Get(I18n.ApiMessages.RequestFailed, (int)response.StatusCode));
            }

            var content = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (content.Length == 0)
                return new ModFileDownloadResult(false, null, null, L.Get(I18n.ApiMessages.InvalidResponse));

            var fileName = ParseDownloadFileName(response.Content.Headers.ContentDisposition)
                ?? $"mod-{id}.zip";

            return new ModFileDownloadResult(true, content, fileName, null);
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            return new ModFileDownloadResult(false, null, null, L.Get(I18n.ApiMessages.Timeout));
        }
        catch (HttpRequestException ex)
        {
            return new ModFileDownloadResult(false, null, null, L.Get(I18n.ApiMessages.CannotConnect, ex.Message));
        }
        catch (Exception ex)
        {
            return new ModFileDownloadResult(false, null, null, L.Get(I18n.ApiMessages.RequestError, ex.Message));
        }
    }

    private static string? ParseDownloadFileName(ContentDispositionHeaderValue? disposition)
    {
        if (disposition is null)
            return null;

        if (!string.IsNullOrWhiteSpace(disposition.FileNameStar))
            return disposition.FileNameStar.Trim('"');

        if (!string.IsNullOrWhiteSpace(disposition.FileName))
            return disposition.FileName.Trim('"');

        var raw = disposition.ToString();
        var utf8Match = Utf8FileNameRegex().Match(raw);
        if (utf8Match.Success)
            return Uri.UnescapeDataString(utf8Match.Groups[1].Value);

        var plainMatch = PlainFileNameRegex().Match(raw);
        return plainMatch.Success ? plainMatch.Groups[1].Value : null;
    }

    [GeneratedRegex(@"filename\*=UTF-8''([^;]+)", RegexOptions.IgnoreCase)]
    private static partial Regex Utf8FileNameRegex();

    [GeneratedRegex(@"filename=""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex PlainFileNameRegex();

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
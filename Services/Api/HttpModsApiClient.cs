using System.Net.Http.Json;
using System.Text.Json;
using UnturnedModLoader.I18n;
using UnturnedModLoader.Models.Api;
using UnturnedModLoader.Services;

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

    public HttpModsApiClient(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        if (httpClient.BaseAddress is not Uri baseAddress)
            throw new ArgumentException("HttpClient.BaseAddress must be set.", nameof(httpClient));

        BaseUrl = baseAddress.AbsoluteUri.TrimEnd('/');
        _http = httpClient;
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
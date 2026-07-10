using Avalonia.Media.Imaging;

namespace UnturnedModLoader.Services;

public sealed class RemoteImageService
{
    private readonly HttpClient _http;

    public RemoteImageService(HttpClient http) => _http = http;

    public static string ResolveUrl(string baseUrl, string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "";

        if (Uri.TryCreate(url, UriKind.Absolute, out _))
            return url;

        var root = baseUrl.TrimEnd('/');
        return url.StartsWith('/') ? root + url : root + "/" + url;
    }

    public async Task<Bitmap?> LoadAsync(string? url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        try
        {
            using var response = await _http.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return new Bitmap(stream);
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }
}
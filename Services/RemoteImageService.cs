using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Media.Imaging;

namespace UnturnedModLoader.Services;

public sealed class RemoteImageService
{
    private readonly HttpClient _http;
    private readonly string _cacheDirectory;
    private readonly ConcurrentDictionary<string, Task<Bitmap?>> _inflight =
        new(StringComparer.OrdinalIgnoreCase);

    public RemoteImageService(HttpClient http)
    {
        _http = http;
        _cacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "UnturnedModLoader",
            "image-cache");
        Directory.CreateDirectory(_cacheDirectory);
    }

    public static string ResolveUrl(string baseUrl, string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "";

        if (Uri.TryCreate(url, UriKind.Absolute, out _))
            return url;

        var root = baseUrl.TrimEnd('/');
        return url.StartsWith('/') ? root + url : root + "/" + url;
    }

    public static Bitmap? LoadLocal(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        try
        {
            return new Bitmap(path);
        }
        catch
        {
            return null;
        }
    }

    public async Task<Bitmap?> LoadAsync(string? url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var normalizedUrl = url.Trim();
        var cachePath = GetCacheFilePath(normalizedUrl);

        var cached = LoadLocal(cachePath);
        if (cached is not null)
            return cached;

        var loadTask = _inflight.GetOrAdd(
            normalizedUrl,
            _ => DownloadAndCacheAsync(normalizedUrl, cachePath, cancellationToken));

        try
        {
            return await loadTask;
        }
        finally
        {
            if (loadTask.IsCompleted)
                _inflight.TryRemove(normalizedUrl, out _);
        }
    }

    private async Task<Bitmap?> DownloadAndCacheAsync(
        string url,
        string cachePath,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (bytes.Length == 0)
                return null;

            await WriteCacheFileAsync(cachePath, bytes, cancellationToken);

            await using var stream = new MemoryStream(bytes);
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

    private string GetCacheFilePath(string url)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant();
        return Path.Combine(_cacheDirectory, hash + GetExtensionFromUrl(url));
    }

    private static string GetExtensionFromUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return ".bin";

        var extension = Path.GetExtension(uri.AbsolutePath);
        if (string.IsNullOrWhiteSpace(extension) || extension.Length > 8)
            return ".bin";

        return extension.ToLowerInvariant();
    }

    private static async Task WriteCacheFileAsync(
        string cachePath,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(cachePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var tempPath = cachePath + ".tmp";
        await File.WriteAllBytesAsync(tempPath, bytes, cancellationToken);
        File.Move(tempPath, cachePath, overwrite: true);
    }
}
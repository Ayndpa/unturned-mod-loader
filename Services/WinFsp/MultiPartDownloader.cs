using System.Net.Http.Headers;

namespace UnturnedModLoader.Services.WinFsp;

/// <summary>
/// Segmented multi-connection downloader. Falls back to a single-stream GET when the server
/// does not advertise Accept-Ranges or Content-Length.
/// </summary>
public static class MultiPartDownloader
{
    private const int DefaultParts = 8;
    private const int MinPartBytes = 256 * 1024;

    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = true })
    {
        Timeout = TimeSpan.FromMinutes(30),
    };

    public sealed record Progress(long BytesReceived, long? TotalBytes, double? Percent);

    public static async Task DownloadAsync(
        string url,
        string destinationPath,
        IProgress<Progress>? progress = null,
        int parts = DefaultParts,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

        long? length = null;
        var acceptsRanges = false;

        try
        {
            using var head = new HttpRequestMessage(HttpMethod.Head, url);
            head.Headers.UserAgent.ParseAdd("UnturnedModLoader");
            using var headResp = await Http.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, ct);
            if (headResp.IsSuccessStatusCode)
            {
                length = headResp.Content.Headers.ContentLength;
                acceptsRanges = headResp.Headers.AcceptRanges.Contains("bytes")
                                || headResp.Content.Headers.ContentRange is not null;
            }
        }
        catch
        {
            // HEAD can fail on some mirrors; fall through to single-stream GET.
        }

        if (length is null or <= 0 || !acceptsRanges || length < MinPartBytes * 2)
        {
            await DownloadSingleAsync(url, destinationPath, length, progress, ct);
            return;
        }

        var total = length.Value;
        var partCount = Math.Clamp(parts, 1, 16);
        while (partCount > 1 && total / partCount < MinPartBytes)
            partCount--;

        var ranges = BuildRanges(total, partCount);
        var partPaths = ranges.Select((_, i) => $"{destinationPath}.part{i}").ToArray();
        var received = new long[ranges.Count];
        var gate = new object();

        void Report()
        {
            long sum;
            lock (gate) sum = received.Sum();
            progress?.Report(new Progress(sum, total, total > 0 ? 100.0 * sum / total : null));
        }

        try
        {
            var tasks = new Task[ranges.Count];
            for (var i = 0; i < ranges.Count; i++)
            {
                var index = i;
                var (start, end) = ranges[i];
                tasks[i] = DownloadRangeAsync(url, partPaths[index], start, end, bytes =>
                {
                    lock (gate) received[index] = bytes;
                    Report();
                }, ct);
            }

            await Task.WhenAll(tasks);

            await using (var output = new FileStream(
                             destinationPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 1024 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                for (var i = 0; i < partPaths.Length; i++)
                {
                    await using var part = new FileStream(
                        partPaths[i],
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        bufferSize: 1024 * 1024,
                        FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);
                    await part.CopyToAsync(output, ct);
                }
            }

            progress?.Report(new Progress(total, total, 100));
        }
        finally
        {
            foreach (var p in partPaths)
            {
                try { if (File.Exists(p)) File.Delete(p); }
                catch { /* ignore */ }
            }
        }
    }

    private static List<(long Start, long End)> BuildRanges(long total, int parts)
    {
        var list = new List<(long, long)>(parts);
        var size = total / parts;
        long cursor = 0;
        for (var i = 0; i < parts; i++)
        {
            var start = cursor;
            var end = i == parts - 1 ? total - 1 : cursor + size - 1;
            list.Add((start, end));
            cursor = end + 1;
        }

        return list;
    }

    private static async Task DownloadRangeAsync(
        string url,
        string partPath,
        long start,
        long end,
        Action<long> onBytes,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd("UnturnedModLoader");
        req.Headers.Range = new RangeHeaderValue(start, end);

        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        await using var input = await resp.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(
            partPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var buffer = new byte[128 * 1024];
        long written = 0;
        int read;
        while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
            written += read;
            onBytes(written);
        }

        var expected = end - start + 1;
        if (written != expected)
            throw new IOException($"Range {start}-{end} incomplete: got {written} of {expected} bytes.");
    }

    private static async Task DownloadSingleAsync(
        string url,
        string destinationPath,
        long? knownLength,
        IProgress<Progress>? progress,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd("UnturnedModLoader");

        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        var total = knownLength ?? resp.Content.Headers.ContentLength;
        await using var input = await resp.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var buffer = new byte[128 * 1024];
        long written = 0;
        int read;
        while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
            written += read;
            progress?.Report(new Progress(
                written,
                total,
                total is > 0 ? 100.0 * written / total.Value : null));
        }

        progress?.Report(new Progress(written, total ?? written, 100));
    }
}

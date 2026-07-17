using System.IO.Pipes;
using System.Text;

namespace UnturnedModLoader.Services;

/// <summary>
/// Ensures only one Mod Loader process runs. A second launch (e.g. from
/// <c>unmod://install/{id}</c>) forwards its args to the existing instance over a named pipe,
/// then exits. The first instance raises <see cref="Activated"/> so the UI can focus and act.
/// </summary>
public sealed class SingleInstanceService : IDisposable
{
    // Local\ = per-session; avoids colliding with other users on the same machine.
    private const string MutexName = @"Local\UnturnedModLoader.SingleInstance";
    private const string PipeName = "UnturnedModLoader.SingleInstance";

    private readonly Mutex? _mutex;
    private readonly CancellationTokenSource _cts = new();
    private bool _ownsMutex;
    private Task? _listenTask;

    /// <summary>Raised on a thread-pool thread when another process forwarded args to us.</summary>
    public event Action<string[]>? Activated;

    private SingleInstanceService(Mutex? mutex, bool ownsMutex)
    {
        _mutex = mutex;
        _ownsMutex = ownsMutex;
    }

    /// <summary>
    /// Try to become the primary instance. Returns the service when we should keep running
    /// (caller should <see cref="StartListening"/>). Returns <c>null</c> when another
    /// instance already owns it — args have already been forwarded and the caller should exit.
    /// </summary>
    public static SingleInstanceService? TryAcquire(string[] args)
    {
        Mutex? mutex = null;
        try
        {
            mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
            if (createdNew)
                return new SingleInstanceService(mutex, ownsMutex: true);

            // Another instance is running — hand off args and exit if the handoff succeeds.
            if (TryForwardArgs(args))
            {
                mutex.Dispose();
                return null;
            }

            // Primary vanished between the mutex check and the pipe connect (or never started
            // its listener). Steal ownership and become the new primary.
            try
            {
                if (mutex.WaitOne(millisecondsTimeout: 500))
                    return new SingleInstanceService(mutex, ownsMutex: true);
            }
            catch (AbandonedMutexException)
            {
                // Previous owner crashed; WaitOne still grants ownership.
                return new SingleInstanceService(mutex, ownsMutex: true);
            }

            mutex.Dispose();
            // Last resort: run without single-instance protection rather than blocking the user.
            return new SingleInstanceService(mutex: null, ownsMutex: false);
        }
        catch
        {
            mutex?.Dispose();
            // Mutex subsystem failure — allow multi-instance so the app still launches.
            return new SingleInstanceService(mutex: null, ownsMutex: false);
        }
    }

    public void StartListening()
    {
        _listenTask = Task.Run(() => ListenLoopAsync(_cts.Token));
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var server = CreateServer();
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

                using var reader = new StreamReader(server, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(line))
                {
                    Activated?.Invoke(Array.Empty<string>());
                    continue;
                }

                Activated?.Invoke(DecodeArgs(line));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Pipe errors are non-fatal; keep listening for the next activation.
                try { await Task.Delay(200, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private static NamedPipeServerStream CreateServer()
    {
        // CurrentUserOnly keeps the pipe private to this Windows user.
        return new NamedPipeServerStream(
            PipeName,
            PipeDirection.In,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }

    /// <returns><c>true</c> if args were delivered to the primary instance.</returns>
    private static bool TryForwardArgs(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(
                ".",
                PipeName,
                PipeDirection.Out,
                PipeOptions.CurrentUserOnly);

            // Short timeout: if the primary is mid-shutdown, fall back to becoming primary.
            client.Connect(timeout: 1500);

            using var writer = new StreamWriter(client, Encoding.UTF8) { AutoFlush = true };
            writer.WriteLine(EncodeArgs(args));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Encode argv as a single line using unit-separator so spaces in paths survive.</summary>
    internal static string EncodeArgs(string[] args) =>
        string.Join('', args.Select(a => a.Replace("", "")));

    internal static string[] DecodeArgs(string line) =>
        string.IsNullOrEmpty(line)
            ? Array.Empty<string>()
            : line.Split('', StringSplitOptions.None);

    public void Dispose()
    {
        _cts.Cancel();
        try { _listenTask?.Wait(millisecondsTimeout: 500); }
        catch { /* ignore */ }

        _cts.Dispose();

        if (_ownsMutex && _mutex is not null)
        {
            try { _mutex.ReleaseMutex(); }
            catch { /* already released / abandoned */ }
            _ownsMutex = false;
        }

        _mutex?.Dispose();
    }
}

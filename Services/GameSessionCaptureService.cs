using System.Collections.Concurrent;
using System.Security.Cryptography;
using UnturnedModLoader.Models;

namespace UnturnedModLoader.Services;

/// <summary>
/// Captures filesystem writes under the game install while the game is running,
/// then absorbs them into the active profile overlay + journal (game + profile changes).
/// Owned roots (Modules/BepInEx via junction) already write into the profile store;
/// this covers sparse new/modified paths the loader did not stage up front.
/// </summary>
public sealed class GameSessionCaptureService : IDisposable
{
    private readonly GameOverlayService _overlay;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, byte> _dirty = new(StringComparer.OrdinalIgnoreCase);

    private string? _profileId;
    private string? _gamePath;
    private Dictionary<string, BaselineEntry>? _baseline;
    private List<FileSystemWatcher>? _watchers;
    private bool _active;

    public GameSessionCaptureService(GameOverlayService overlay)
    {
        _overlay = overlay;
    }

    public bool IsActive
    {
        get
        {
            lock (_gate) return _active;
        }
    }

    public string? ActiveProfileId
    {
        get
        {
            lock (_gate) return _profileId;
        }
    }

    /// <summary>Begin watching. No-op for invalid path.</summary>
    public void Start(string profileId, string gamePath)
    {
        lock (_gate)
        {
            StopWatchers_NoLock();
            _dirty.Clear();
            _baseline = null;
            _profileId = null;
            _gamePath = null;
            _active = false;

            if (!GamePathValidator.IsValid(gamePath))
                return;

            var fullGame = Path.GetFullPath(gamePath);
            _profileId = profileId;
            _gamePath = fullGame;
            AppPaths.EnsureProfileLayout(profileId);

            // Metadata baseline + content backups for small non-owned files (for true vanilla restore).
            _baseline = BuildBaseline(fullGame, profileId);
            _watchers = CreateWatchers(fullGame);
            _active = true;
        }
    }

    /// <summary>
    /// Stop watching and absorb new/modified paths into the profile.
    /// Safe to call when not active.
    /// </summary>
    public CaptureResult StopAndAbsorb()
    {
        string? profileId;
        string? gamePath;
        Dictionary<string, BaselineEntry>? baseline;

        lock (_gate)
        {
            if (!_active || _profileId is null || _gamePath is null || _baseline is null)
            {
                StopWatchers_NoLock();
                _active = false;
                return CaptureResult.Empty;
            }

            profileId = _profileId;
            gamePath = _gamePath;
            baseline = _baseline;
            StopWatchers_NoLock();
            _active = false;
            _profileId = null;
            _gamePath = null;
            _baseline = null;
        }

        try
        {
            var result = _overlay.AbsorbRuntimeChanges(profileId, gamePath, baseline);
            CleanupSessionBaseline(profileId);
            return result;
        }
        catch (Exception ex)
        {
            CleanupSessionBaseline(profileId);
            return CaptureResult.Failed(ex.Message);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            StopWatchers_NoLock();
            _active = false;
        }
    }

    private List<FileSystemWatcher> CreateWatchers(string gamePath)
    {
        var list = new List<FileSystemWatcher>();
        try
        {
            var rootWatcher = new FileSystemWatcher(gamePath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName
                               | NotifyFilters.DirectoryName
                               | NotifyFilters.LastWrite
                               | NotifyFilters.Size
                               | NotifyFilters.CreationTime,
                InternalBufferSize = 64 * 1024,
                EnableRaisingEvents = true,
            };
            rootWatcher.Created += OnFsEvent;
            rootWatcher.Changed += OnFsEvent;
            rootWatcher.Renamed += OnFsRenamed;
            rootWatcher.Deleted += OnFsEvent;
            rootWatcher.Error += (_, _) => { /* buffer overflow — end scan still authoritative */ };
            list.Add(rootWatcher);
        }
        catch
        {
            // Watcher is best-effort; end-of-session full diff still runs.
        }

        return list;
    }

    private void OnFsEvent(object sender, FileSystemEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.FullPath))
            return;
        _dirty[e.FullPath] = 0;
    }

    private void OnFsRenamed(object sender, RenamedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(e.OldFullPath))
            _dirty[e.OldFullPath] = 0;
        if (!string.IsNullOrWhiteSpace(e.FullPath))
            _dirty[e.FullPath] = 0;
    }

    private void StopWatchers_NoLock()
    {
        if (_watchers is null)
            return;

        foreach (var w in _watchers)
        {
            try
            {
                w.EnableRaisingEvents = false;
                w.Created -= OnFsEvent;
                w.Changed -= OnFsEvent;
                w.Renamed -= OnFsRenamed;
                w.Deleted -= OnFsEvent;
                w.Dispose();
            }
            catch
            {
                // ignore
            }
        }

        _watchers = null;
    }

    private static Dictionary<string, BaselineEntry> BuildBaseline(string gamePath, string profileId)
    {
        var map = new Dictionary<string, BaselineEntry>(StringComparer.OrdinalIgnoreCase);
        var sessionBaselineRoot = AppPaths.ProfileSessionBaselineDir(profileId);
        try
        {
            if (Directory.Exists(sessionBaselineRoot))
                Directory.Delete(sessionBaselineRoot, recursive: true);
        }
        catch
        {
            // ignore
        }

        Directory.CreateDirectory(sessionBaselineRoot);

        long contentBytes = 0;
        const long maxContentBytes = 1L * 1024 * 1024 * 1024; // 1 GB cap
        const long maxFileBytes = 8L * 1024 * 1024; // 8 MB per file

        foreach (var file in EnumerateGameFiles(gamePath))
        {
            string rel;
            try
            {
                rel = Path.GetRelativePath(gamePath, file);
            }
            catch
            {
                continue;
            }

            if (IsIgnoredRelative(rel))
                continue;

            long length = 0;
            DateTime writeUtc = DateTime.MinValue;
            try
            {
                var info = new FileInfo(file);
                length = info.Length;
                writeUtc = info.LastWriteTimeUtc;
            }
            catch
            {
                continue;
            }

            var owned = OverlayOwnedRoots.IsOwnedRoot(rel);
            string? contentBackupRel = null;

            // Pre-backup small vanilla/sparse files so runtime overwrites can be restored.
            if (!owned && length > 0 && length <= maxFileBytes && contentBytes + length <= maxContentBytes)
            {
                try
                {
                    var backup = Path.Combine(sessionBaselineRoot, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Copy(file, backup, overwrite: true);
                    contentBackupRel = rel;
                    contentBytes += length;
                }
                catch
                {
                    contentBackupRel = null;
                }
            }

            map[NormalizeKey(rel)] = new BaselineEntry
            {
                RelativePath = rel,
                Length = length,
                LastWriteUtc = writeUtc,
                IsOwnedRoot = owned,
                SessionBackupRelativePath = contentBackupRel,
            };
        }

        // Also record directories for structure (optional empty markers not required).
        return map;
    }

    private static void CleanupSessionBaseline(string profileId)
    {
        try
        {
            var dir = AppPaths.ProfileSessionBaselineDir(profileId);
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // ignore
        }
    }

    internal static IEnumerable<string> EnumerateGameFiles(string gamePath)
    {
        var stack = new Stack<string>();
        stack.Push(gamePath);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            IEnumerable<string> subdirs = [];
            IEnumerable<string> files = [];
            try
            {
                subdirs = Directory.EnumerateDirectories(dir);
                files = Directory.EnumerateFiles(dir);
            }
            catch
            {
                continue;
            }

            foreach (var sub in subdirs)
            {
                var name = Path.GetFileName(sub);
                // Skip reparse noise handled separately; still walk junction targets for baseline of owned content.
                if (string.Equals(name, "node_modules", StringComparison.OrdinalIgnoreCase))
                    continue;
                stack.Push(sub);
            }

            foreach (var file in files)
                yield return file;
        }
    }

    internal static bool IsIgnoredRelative(string relativePath)
    {
        var name = Path.GetFileName(relativePath);
        if (name.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
            return true;
        if (name.Equals("output_log.txt", StringComparison.OrdinalIgnoreCase))
            return true;
        if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            return true;
        if (name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
            return true;

        // Crash dumps / huge telemetry — still capturable if needed; skip common junk.
        if (name.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    internal static string NormalizeKey(string relativePath) =>
        relativePath.Replace('/', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);
}

public sealed class BaselineEntry
{
    public string RelativePath { get; init; } = "";
    public long Length { get; init; }
    public DateTime LastWriteUtc { get; init; }
    public bool IsOwnedRoot { get; init; }
    public string? SessionBackupRelativePath { get; init; }
}

public sealed class CaptureResult
{
    public bool Success { get; init; }
    public int NewFiles { get; init; }
    public int ModifiedFiles { get; init; }
    public int NewDirectories { get; init; }
    public string? Error { get; init; }

    public static CaptureResult Empty { get; } = new() { Success = true };

    public static CaptureResult Failed(string error) => new() { Success = false, Error = error };

    public string SummaryMessage()
    {
        if (!Success)
            return Error ?? "Capture failed.";
        if (NewFiles == 0 && ModifiedFiles == 0 && NewDirectories == 0)
            return "";
        return $"Captured runtime changes: +{NewFiles} files, ~{ModifiedFiles} modified, +{NewDirectories} dirs.";
    }
}

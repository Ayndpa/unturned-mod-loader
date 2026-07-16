using System.Text.Json;
using UnturnedModLoader.Models;

namespace UnturnedModLoader.Services;

/// <summary>
/// Applies a profile's <c>overlay\</c> tree onto the game install with full journaled rollback.
/// Owned roots (Modules, BepInEx, …) use directory junctions; other paths use copy + originals backup.
/// Each profile is game install + overlay changes (virtual tree); switching profiles remounts the active overlay.
/// </summary>
public sealed class GameOverlayService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly ProfileMountService _junctions = new();

    public MountResult EnsureApplied(GameProfile profile, string gamePath) =>
        Apply(profile, gamePath);

    public MountResult Apply(GameProfile profile, string gamePath)
    {
        if (!OperatingSystem.IsWindows())
            return MountResult.Fail("Profile overlays require Windows.");

        if (!GamePathValidator.IsValid(gamePath))
            return MountResult.Fail("Game path is not valid.");

        if (GameProcessService.IsRunning(gamePath))
            return MountResult.Fail("Game is running.");

        // Tear down whatever is currently applied (any profile / path).
        var active = LoadActiveState();
        if (!string.IsNullOrWhiteSpace(active.GamePath))
        {
            var un = UnapplyAll(active.GamePath, ignoreGameRunning: false);
            if (!un.Success)
                return un;
        }
        else if (GamePathValidator.IsValid(gamePath))
        {
            var un = UnapplyAll(gamePath, ignoreGameRunning: false);
            if (!un.Success)
                return un;
        }

        AppPaths.EnsureProfileLayout(profile.Id);
        var overlayRoot = AppPaths.ProfileOverlayDir(profile.Id);
        var originalsRoot = AppPaths.ProfileOriginalsDir(profile.Id);
        Directory.CreateDirectory(overlayRoot);
        Directory.CreateDirectory(originalsRoot);

        var fullGame = Path.GetFullPath(gamePath);
        var journal = new OverlayJournal
        {
            ProfileId = profile.Id,
            GamePath = fullGame,
            Ops = [],
        };

        try
        {
            // 1) Owned roots → single junction each when present under overlay.
            foreach (var owned in OverlayOwnedRoots.All)
            {
                var overlayOwned = Path.Combine(overlayRoot, owned);
                if (!Directory.Exists(overlayOwned))
                    continue;

                // Only mount if the profile actually uses this root (has any entry) OR it's Modules (always keep ready).
                var hasContent = Directory.EnumerateFileSystemEntries(overlayOwned).Any()
                                 || string.Equals(owned, "Modules", StringComparison.OrdinalIgnoreCase);
                if (!hasContent)
                    continue;

                var gameOwned = Path.Combine(fullGame, owned);
                var prepare = PrepareJunctionTarget(gameOwned, overlayOwned);
                if (!prepare.Success)
                {
                    RollbackJournal(journal, originalsRoot);
                    return prepare;
                }

                if (!JunctionHelper.IsJunction(gameOwned))
                    JunctionHelper.CreateJunction(gameOwned, Path.GetFullPath(overlayOwned));

                journal.Ops.Add(new OverlayOp
                {
                    Kind = OverlayOpKind.Junction,
                    RelativePath = owned,
                    SourcePath = Path.GetFullPath(overlayOwned),
                });
            }

            // 2) Sparse overlay files/dirs outside owned roots.
            ApplySparseOverlay(overlayRoot, fullGame, originalsRoot, journal);

            SaveJournal(profile.Id, journal);
            SaveActiveState(new OverlayJournal
            {
                ProfileId = profile.Id,
                GamePath = fullGame,
                Ops = journal.Ops.ToList(),
            });

            // Keep legacy mount-state in sync for older repair paths.
            _junctions.SaveState(new MountState
            {
                AppliedProfileId = profile.Id,
                GamePath = fullGame,
                Mounts = journal.Ops
                    .Where(o => o.Kind == OverlayOpKind.Junction)
                    .Select(o => new AppliedMount
                    {
                        MountId = o.RelativePath.ToLowerInvariant(),
                        GameRelativePath = o.RelativePath,
                        SourcePath = o.SourcePath ?? "",
                        Kind = "Junction",
                    })
                    .ToList(),
            });

            return MountResult.Ok();
        }
        catch (Exception ex)
        {
            RollbackJournal(journal, originalsRoot);
            return MountResult.Fail(ex.Message);
        }
    }

    public MountResult UnapplyAll(string gamePath, bool ignoreGameRunning = false)
    {
        if (!OperatingSystem.IsWindows())
            return MountResult.Ok();

        if (!ignoreGameRunning && GamePathValidator.IsValid(gamePath) && GameProcessService.IsRunning(gamePath))
            return MountResult.Fail("Game is running.");

        var errors = new List<string>();
        var active = LoadActiveState();

        // Prefer active journal; also try per-profile journals that claim this game path.
        var journals = new List<OverlayJournal>();
        if (active.Ops.Count > 0)
            journals.Add(active);

        if (Directory.Exists(AppPaths.ProfilesRoot))
        {
            foreach (var dir in Directory.EnumerateDirectories(AppPaths.ProfilesRoot))
            {
                var id = Path.GetFileName(dir);
                var j = LoadJournal(id);
                if (j.Ops.Count == 0)
                    continue;
                if (!string.IsNullOrWhiteSpace(j.GamePath) && PathsEqual(j.GamePath, gamePath))
                    journals.Add(j);
            }
        }

        // Also clear owned junctions even without journal (repair).
        foreach (var owned in OverlayOwnedRoots.All)
        {
            try
            {
                var path = Path.Combine(gamePath, owned);
                if (JunctionHelper.IsJunction(path) && IsUnderProfiles(path))
                    JunctionHelper.DeleteJunction(path);
            }
            catch (Exception ex)
            {
                errors.Add(ex.Message);
            }
        }

        foreach (var journal in journals.DistinctBy(j => (j.ProfileId, j.GamePath)))
        {
            var originals = string.IsNullOrWhiteSpace(journal.ProfileId)
                ? ""
                : AppPaths.ProfileOriginalsDir(journal.ProfileId);
            try
            {
                RollbackJournal(journal, originals);
                if (!string.IsNullOrWhiteSpace(journal.ProfileId))
                    SaveJournal(journal.ProfileId, new OverlayJournal { ProfileId = journal.ProfileId });
            }
            catch (Exception ex)
            {
                errors.Add(ex.Message);
            }
        }

        // Stale path in active state.
        if (!string.IsNullOrWhiteSpace(active.GamePath) && !PathsEqual(active.GamePath, gamePath))
        {
            try
            {
                UnapplyAll(active.GamePath, ignoreGameRunning: true);
            }
            catch
            {
                // ignore nested
            }
        }

        SaveActiveState(new OverlayJournal());
        _junctions.SaveState(new MountState());

        return errors.Count == 0
            ? MountResult.Ok()
            : MountResult.Fail(string.Join("; ", errors));
    }

    /// <summary>Stage a file into the profile overlay (relative to game root). Does not apply live.</summary>
    public void StageFile(string profileId, string gameRelativePath, Stream content)
    {
        AppPaths.EnsureProfileLayout(profileId);
        var rel = NormalizeRelative(gameRelativePath);
        var dest = Path.Combine(AppPaths.ProfileOverlayDir(profileId), rel);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        using var fs = File.Create(dest);
        content.CopyTo(fs);
    }

    /// <summary>Stage raw bytes into overlay.</summary>
    public void StageFile(string profileId, string gameRelativePath, byte[] content)
    {
        using var ms = new MemoryStream(content);
        StageFile(profileId, gameRelativePath, ms);
    }

    /// <summary>Copy an existing file/directory into the profile overlay under a game-relative path.</summary>
    public void StagePath(string profileId, string gameRelativePath, string sourcePath)
    {
        AppPaths.EnsureProfileLayout(profileId);
        var rel = NormalizeRelative(gameRelativePath);
        var dest = Path.Combine(AppPaths.ProfileOverlayDir(profileId), rel);

        if (Directory.Exists(sourcePath))
        {
            CopyDirectory(sourcePath, dest);
        }
        else if (File.Exists(sourcePath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(sourcePath, dest, overwrite: true);
        }
        else
        {
            throw new FileNotFoundException("Source path not found.", sourcePath);
        }
    }

    public string GetOverlayPath(string profileId, string gameRelativePath) =>
        Path.Combine(AppPaths.ProfileOverlayDir(profileId), NormalizeRelative(gameRelativePath));

    /// <summary>
    /// Diff game install against a pre-run baseline and fold runtime writes into the profile.
    /// Owned-root paths (junction targets) already live in overlay and are skipped.
    /// New files → overlay + CreateFile; modified files → originals backup + overlay + ReplaceFile.
    /// </summary>
    public CaptureResult AbsorbRuntimeChanges(
        string profileId,
        string gamePath,
        IReadOnlyDictionary<string, BaselineEntry> baseline)
    {
        if (!GamePathValidator.IsValid(gamePath))
            return CaptureResult.Failed("Game path is not valid.");

        AppPaths.EnsureProfileLayout(profileId);
        var fullGame = Path.GetFullPath(gamePath);
        var overlayRoot = AppPaths.ProfileOverlayDir(profileId);
        var originalsRoot = AppPaths.ProfileOriginalsDir(profileId);
        var sessionBaselineRoot = AppPaths.ProfileSessionBaselineDir(profileId);

        var journal = LoadJournal(profileId);
        if (string.IsNullOrWhiteSpace(journal.GamePath))
            journal.GamePath = fullGame;
        journal.ProfileId = profileId;

        var knownOps = new HashSet<string>(
            journal.Ops.Select(o => $"{o.Kind}|{NormalizeKey(o.RelativePath)}"),
            StringComparer.OrdinalIgnoreCase);

        var newFiles = 0;
        var modifiedFiles = 0;
        var newDirs = 0;

        // Track directories that exist now for CreateDirectory bookkeeping.
        var seenDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in GameSessionCaptureService.EnumerateGameFiles(fullGame))
        {
            string rel;
            try
            {
                rel = Path.GetRelativePath(fullGame, file);
            }
            catch
            {
                continue;
            }

            if (GameSessionCaptureService.IsIgnoredRelative(rel))
                continue;

            var key = NormalizeKey(rel);

            // Junction-owned trees already write into profile overlay.
            if (OverlayOwnedRoots.IsOwnedRoot(rel))
                continue;

            // Ensure parent CreateDirectory ops for brand-new folder trees.
            RecordNewDirectoryChain(rel, fullGame, baseline, journal, knownOps, ref newDirs, seenDirs);

            baseline.TryGetValue(key, out var before);

            long length = 0;
            DateTime writeUtc = DateTime.MinValue;
            try
            {
                var info = new FileInfo(file);
                if (!info.Exists)
                    continue;
                length = info.Length;
                writeUtc = info.LastWriteTimeUtc;
            }
            catch
            {
                continue;
            }

            var isNew = before is null;
            var isModified = before is not null &&
                             (before.Length != length ||
                              Math.Abs((before.LastWriteUtc - writeUtc).TotalSeconds) > 1.0);

            if (!isNew && !isModified)
                continue;

            try
            {
                // Stage current game file into overlay so re-apply restores this profile's version.
                var overlayDest = Path.Combine(overlayRoot, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(overlayDest)!);
                File.Copy(file, overlayDest, overwrite: true);

                if (isNew)
                {
                    var opKey = $"{OverlayOpKind.CreateFile}|{key}";
                    if (!knownOps.Contains(opKey))
                    {
                        journal.Ops.Add(new OverlayOp
                        {
                            Kind = OverlayOpKind.CreateFile,
                            RelativePath = rel,
                        });
                        knownOps.Add(opKey);
                    }

                    newFiles++;
                }
                else
                {
                    // Prefer session pre-run backup for true vanilla restore.
                    var originalsDest = Path.Combine(originalsRoot, rel);
                    if (!File.Exists(originalsDest))
                    {
                        var sessionBackup = before!.SessionBackupRelativePath is null
                            ? null
                            : Path.Combine(sessionBaselineRoot, before.SessionBackupRelativePath);

                        if (sessionBackup is not null && File.Exists(sessionBackup))
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(originalsDest)!);
                            File.Copy(sessionBackup, originalsDest, overwrite: false);
                        }
                    }

                    var opKey = $"{OverlayOpKind.ReplaceFile}|{key}";
                    if (!knownOps.Contains(opKey) &&
                        !knownOps.Contains($"{OverlayOpKind.CreateFile}|{key}"))
                    {
                        journal.Ops.Add(new OverlayOp
                        {
                            Kind = OverlayOpKind.ReplaceFile,
                            RelativePath = rel,
                            OriginalsRelativePath = rel,
                        });
                        knownOps.Add(opKey);
                    }

                    modifiedFiles++;
                }
            }
            catch
            {
                // Skip locked/unreadable files; next session can pick them up.
            }
        }

        // Detect directories created under game root that are not owned roots.
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(fullGame, "*", SearchOption.AllDirectories))
            {
                string rel;
                try
                {
                    rel = Path.GetRelativePath(fullGame, dir);
                }
                catch
                {
                    continue;
                }

                if (OverlayOwnedRoots.IsOwnedRoot(rel))
                    continue;

                // Skip if this path was a junction we own.
                if (JunctionHelper.IsJunction(dir))
                    continue;

                var key = NormalizeKey(rel);
                // Directory is "new" if no baseline file lived under this prefix and path did not exist as file.
                // We only record CreateDirectory when the directory itself is empty of baseline files
                // and wasn't present as any baseline path prefix... simpler: if no baseline entry has this as parent
                // and directory was created during session — approximate: no baseline key starts with?
                // Actually: if no file in baseline had this exact directory as ancestor from pre-scan of files only.
                // Use: directory is new if none of baseline keys start with rel + sep AND no file baseline equals.
                var prefix = key + Path.DirectorySeparatorChar;
                var hadBaselineUnder = baseline.Keys.Any(k =>
                    k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(k, key, StringComparison.OrdinalIgnoreCase));

                if (hadBaselineUnder)
                    continue;

                var opKey = $"{OverlayOpKind.CreateDirectory}|{key}";
                if (knownOps.Contains(opKey))
                    continue;

                // Only if directory still exists (it does).
                journal.Ops.Add(new OverlayOp
                {
                    Kind = OverlayOpKind.CreateDirectory,
                    RelativePath = rel,
                });
                knownOps.Add(opKey);
                newDirs++;
            }
        }
        catch
        {
            // directory scan best-effort
        }

        SaveJournal(profileId, journal);

        // Merge into active overlay state so UnapplyAll sees the new ops immediately.
        var active = LoadActiveState();
        if (string.Equals(active.ProfileId, profileId, StringComparison.OrdinalIgnoreCase) ||
            active.Ops.Count == 0)
        {
            SaveActiveState(new OverlayJournal
            {
                ProfileId = profileId,
                GamePath = fullGame,
                Ops = journal.Ops.ToList(),
            });
        }
        else
        {
            // Still update active if it points at same game path.
            if (!string.IsNullOrWhiteSpace(active.GamePath) && PathsEqual(active.GamePath, fullGame))
            {
                SaveActiveState(new OverlayJournal
                {
                    ProfileId = profileId,
                    GamePath = fullGame,
                    Ops = journal.Ops.ToList(),
                });
            }
        }

        return new CaptureResult
        {
            Success = true,
            NewFiles = newFiles,
            ModifiedFiles = modifiedFiles,
            NewDirectories = newDirs,
        };
    }

    private static void RecordNewDirectoryChain(
        string fileRelativePath,
        string gameRoot,
        IReadOnlyDictionary<string, BaselineEntry> baseline,
        OverlayJournal journal,
        HashSet<string> knownOps,
        ref int newDirs,
        HashSet<string> seenDirs)
    {
        var parts = fileRelativePath
            .Replace('/', Path.DirectorySeparatorChar)
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length <= 1)
            return;

        var accum = "";
        for (var i = 0; i < parts.Length - 1; i++)
        {
            accum = i == 0 ? parts[0] : Path.Combine(accum, parts[i]);
            var key = NormalizeKey(accum);
            if (!seenDirs.Add(key))
                continue;

            if (OverlayOwnedRoots.IsOwnedRoot(accum))
                continue;

            // If any baseline file lived under this directory, it already existed.
            var prefix = key + Path.DirectorySeparatorChar;
            var existed = baseline.Keys.Any(k =>
                k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            if (existed)
                continue;

            var opKey = $"{OverlayOpKind.CreateDirectory}|{key}";
            if (knownOps.Contains(opKey))
                continue;

            var abs = Path.Combine(gameRoot, accum);
            if (!Directory.Exists(abs))
                continue;

            journal.Ops.Add(new OverlayOp
            {
                Kind = OverlayOpKind.CreateDirectory,
                RelativePath = accum,
            });
            knownOps.Add(opKey);
            newDirs++;
        }
    }

    private static string NormalizeKey(string relativePath) =>
        relativePath.Replace('/', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);

    private void ApplySparseOverlay(
        string overlayRoot,
        string gameRoot,
        string originalsRoot,
        OverlayJournal journal)
    {
        if (!Directory.Exists(overlayRoot))
            return;

        foreach (var entry in Directory.EnumerateFileSystemEntries(overlayRoot))
        {
            var name = Path.GetFileName(entry);
            if (OverlayOwnedRoots.All.Any(r => string.Equals(r, name, StringComparison.OrdinalIgnoreCase)))
                continue; // handled as junction

            if (Directory.Exists(entry))
                ApplyDirectoryRecursive(entry, Path.Combine(gameRoot, name), name, originalsRoot, journal);
            else if (File.Exists(entry))
                ApplyFile(entry, Path.Combine(gameRoot, name), name, originalsRoot, journal);
        }
    }

    private void ApplyDirectoryRecursive(
        string overlayDir,
        string gameDir,
        string relativeDir,
        string originalsRoot,
        OverlayJournal journal)
    {
        if (!Directory.Exists(gameDir))
        {
            Directory.CreateDirectory(gameDir);
            journal.Ops.Add(new OverlayOp
            {
                Kind = OverlayOpKind.CreateDirectory,
                RelativePath = relativeDir,
            });
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(overlayDir))
        {
            var name = Path.GetFileName(entry);
            var rel = Path.Combine(relativeDir, name);
            if (Directory.Exists(entry))
                ApplyDirectoryRecursive(entry, Path.Combine(gameDir, name), rel, originalsRoot, journal);
            else if (File.Exists(entry))
                ApplyFile(entry, Path.Combine(gameDir, name), rel, originalsRoot, journal);
        }
    }

    private void ApplyFile(
        string overlayFile,
        string gameFile,
        string relativePath,
        string originalsRoot,
        OverlayJournal journal)
    {
        var gameDir = Path.GetDirectoryName(gameFile);
        if (!string.IsNullOrWhiteSpace(gameDir) && !Directory.Exists(gameDir))
        {
            // Ensure parent chain; record only dirs we create.
            CreateDirectoryChain(gameDir, GetGameRootFrom(gameFile, relativePath), journal);
        }

        if (File.Exists(gameFile))
        {
            // Backup vanilla/original then replace.
            var originalsPath = Path.Combine(originalsRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(originalsPath)!);
            if (!File.Exists(originalsPath))
                File.Copy(gameFile, originalsPath, overwrite: false);

            File.Copy(overlayFile, gameFile, overwrite: true);
            journal.Ops.Add(new OverlayOp
            {
                Kind = OverlayOpKind.ReplaceFile,
                RelativePath = relativePath,
                OriginalsRelativePath = relativePath,
            });
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(gameFile)!);
            File.Copy(overlayFile, gameFile, overwrite: true);
            journal.Ops.Add(new OverlayOp
            {
                Kind = OverlayOpKind.CreateFile,
                RelativePath = relativePath,
            });
        }
    }

    private static void CreateDirectoryChain(string absoluteDir, string gameRoot, OverlayJournal journal)
    {
        var fullRoot = Path.GetFullPath(gameRoot);
        var fullDir = Path.GetFullPath(absoluteDir);
        if (!fullDir.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            return;

        var rel = Path.GetRelativePath(fullRoot, fullDir);
        var parts = rel.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var current = fullRoot;
        var relAccum = "";
        foreach (var part in parts)
        {
            current = Path.Combine(current, part);
            relAccum = string.IsNullOrEmpty(relAccum) ? part : Path.Combine(relAccum, part);
            if (!Directory.Exists(current))
            {
                Directory.CreateDirectory(current);
                if (!journal.Ops.Any(o =>
                        o.Kind == OverlayOpKind.CreateDirectory &&
                        string.Equals(o.RelativePath, relAccum, StringComparison.OrdinalIgnoreCase)))
                {
                    journal.Ops.Add(new OverlayOp
                    {
                        Kind = OverlayOpKind.CreateDirectory,
                        RelativePath = relAccum,
                    });
                }
            }
        }
    }

    private static string GetGameRootFrom(string gameFile, string relativePath)
    {
        var full = Path.GetFullPath(gameFile);
        var rel = relativePath.Replace('/', Path.DirectorySeparatorChar);
        if (full.EndsWith(rel, StringComparison.OrdinalIgnoreCase))
            return full[..^rel.Length].TrimEnd(Path.DirectorySeparatorChar);
        return Path.GetDirectoryName(full) ?? full;
    }

    private void RollbackJournal(OverlayJournal journal, string originalsRoot)
    {
        if (journal.Ops.Count == 0)
            return;

        var gameRoot = journal.GamePath;
        if (string.IsNullOrWhiteSpace(gameRoot))
            return;

        // Reverse order for safe undo.
        foreach (var op in journal.Ops.AsEnumerable().Reverse())
        {
            try
            {
                var target = Path.Combine(gameRoot, op.RelativePath);
                switch (op.Kind)
                {
                    case OverlayOpKind.Junction:
                        if (Directory.Exists(target) && JunctionHelper.IsJunction(target))
                            JunctionHelper.DeleteJunction(target);
                        break;

                    case OverlayOpKind.CreateFile:
                        if (File.Exists(target))
                            File.Delete(target);
                        TryDeleteEmptyParents(target, gameRoot);
                        break;

                    case OverlayOpKind.CreateDirectory:
                        if (Directory.Exists(target) &&
                            !Directory.EnumerateFileSystemEntries(target).Any() &&
                            !JunctionHelper.IsJunction(target))
                        {
                            Directory.Delete(target);
                        }
                        TryDeleteEmptyParents(target, gameRoot);
                        break;

                    case OverlayOpKind.ReplaceFile:
                        var originalsRel = op.OriginalsRelativePath ?? op.RelativePath;
                        var backup = Path.Combine(originalsRoot, originalsRel);
                        if (File.Exists(backup))
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                            File.Copy(backup, target, overwrite: true);
                        }
                        break;
                }
            }
            catch
            {
                // continue other ops
            }
        }
    }

    private static void TryDeleteEmptyParents(string fileOrDir, string gameRoot)
    {
        try
        {
            var dir = Directory.Exists(fileOrDir) ? fileOrDir : Path.GetDirectoryName(fileOrDir);
            var root = Path.GetFullPath(gameRoot);
            while (!string.IsNullOrWhiteSpace(dir))
            {
                var full = Path.GetFullPath(dir);
                if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
                    break;
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    break;
                if (!Directory.Exists(full))
                    break;
                if (Directory.EnumerateFileSystemEntries(full).Any())
                    break;
                Directory.Delete(full);
                dir = Path.GetDirectoryName(full);
            }
        }
        catch
        {
            // ignore
        }
    }

    private MountResult PrepareJunctionTarget(string target, string source)
    {
        if (!Directory.Exists(target) && !File.Exists(target))
            return MountResult.Ok();

        if (JunctionHelper.IsJunction(target))
        {
            if (JunctionHelper.TryGetJunctionTarget(target, out var existing) &&
                existing is not null &&
                PathsEqual(existing, source))
            {
                return MountResult.Ok();
            }

            try
            {
                JunctionHelper.DeleteJunction(target);
                return MountResult.Ok();
            }
            catch (Exception ex)
            {
                return MountResult.Fail(ex.Message);
            }
        }

        if (Directory.Exists(target))
        {
            // Real directory with content blocks mount — migration should have moved it.
            if (Directory.EnumerateFileSystemEntries(target).Any())
            {
                return MountResult.Fail(
                    $"Game folder '{Path.GetFileName(target)}' already contains files that are not managed by a profile. Move or clear it first.");
            }

            try
            {
                Directory.Delete(target);
                return MountResult.Ok();
            }
            catch (Exception ex)
            {
                return MountResult.Fail(ex.Message);
            }
        }

        return MountResult.Fail($"Cannot replace file at mount target: {target}");
    }

    private bool IsUnderProfiles(string junctionPath)
    {
        if (!JunctionHelper.TryGetJunctionTarget(junctionPath, out var target) || target is null)
            return false;
        try
        {
            var profiles = Path.GetFullPath(AppPaths.ProfilesRoot)
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(target).StartsWith(profiles, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private OverlayJournal LoadActiveState()
    {
        AppPaths.EnsureAppData();
        if (!File.Exists(AppPaths.ActiveOverlayStatePath))
            return new OverlayJournal();
        try
        {
            var json = File.ReadAllText(AppPaths.ActiveOverlayStatePath);
            return JsonSerializer.Deserialize<OverlayJournal>(json, JsonOptions) ?? new OverlayJournal();
        }
        catch
        {
            return new OverlayJournal();
        }
    }

    private void SaveActiveState(OverlayJournal state)
    {
        AppPaths.EnsureAppData();
        File.WriteAllText(AppPaths.ActiveOverlayStatePath, JsonSerializer.Serialize(state, JsonOptions));
    }

    private OverlayJournal LoadJournal(string profileId)
    {
        var path = AppPaths.ProfileJournalPath(profileId);
        if (!File.Exists(path))
            return new OverlayJournal { ProfileId = profileId };
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<OverlayJournal>(json, JsonOptions)
                   ?? new OverlayJournal { ProfileId = profileId };
        }
        catch
        {
            return new OverlayJournal { ProfileId = profileId };
        }
    }

    private void SaveJournal(string profileId, OverlayJournal journal)
    {
        AppPaths.EnsureProfileLayout(profileId);
        journal.ProfileId = profileId;
        File.WriteAllText(AppPaths.ProfileJournalPath(profileId), JsonSerializer.Serialize(journal, JsonOptions));
    }

    private static string NormalizeRelative(string relative)
    {
        var rel = relative.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        if (rel.Contains("..", StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid relative path.");
        return rel;
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            var name = Path.GetFileName(file);
            File.Copy(file, Path.Combine(dest, name), overwrite: true);
        }

        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            var name = Path.GetFileName(dir);
            CopyDirectory(dir, Path.Combine(dest, name));
        }
    }

    private static bool PathsEqual(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(a).TrimEnd('\\', '/'),
                Path.GetFullPath(b).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }
}

using System.Text.Json;
using UnturnedModLoader.Models;

namespace UnturnedModLoader.Services;

/// <summary>
/// Tracks installed mods as one manifest file per RemoteId under the profile overlay's
/// <c>.unmod-manifests\</c> directory. Each manifest records the remote metadata plus the
/// exact list of files the package extracted into the overlay root, so uninstall can remove
/// exactly what was written. No scanning of the game/modules folder is performed.
/// </summary>
public sealed class InstalledModsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private string _profileId = "";
    private string _overlayRoot = "";
    private string _manifestsDir = "";

    public InstalledModsService()
    {
        AppPaths.EnsureAppData();
    }

    /// <summary>Overlay root (= game root via VFS). Mods extract directly into here.</summary>
    public string OverlayRoot => _overlayRoot;

    public void UseProfile(string profileId)
    {
        _profileId = profileId;

        if (string.IsNullOrWhiteSpace(profileId))
        {
            _overlayRoot = "";
            _manifestsDir = "";
            return;
        }

        AppPaths.EnsureProfileLayout(profileId);
        _overlayRoot = AppPaths.ProfileOverlayDir(profileId);
        _manifestsDir = AppPaths.ProfileManifestsDir(profileId);
        Directory.CreateDirectory(_manifestsDir);
    }

    /// <summary>Load every installed mod manifest, newest first.</summary>
    public IReadOnlyList<InstalledMod> GetAll()
    {
        if (string.IsNullOrWhiteSpace(_manifestsDir) || !Directory.Exists(_manifestsDir))
            return [];

        var mods = new List<InstalledMod>();
        foreach (var file in Directory.EnumerateFiles(_manifestsDir, "*.json"))
        {
            var mod = LoadManifestFile(file);
            if (mod is not null)
                mods.Add(mod);
        }

        return mods
            .OrderByDescending(m => m.InstalledAt)
            .ToList();
    }

    /// <summary>
    /// Write the install manifest for <paramref name="entry"/>'s RemoteId. If a manifest
    /// already exists for that RemoteId, files recorded there but absent from the new list
    /// are removed first, so a version upgrade does not leave stale files behind.
    /// </summary>
    public void RecordInstall(InstalledMod entry)
    {
        if (string.IsNullOrWhiteSpace(_manifestsDir) || entry.RemoteId is null)
            return;

        var path = ManifestPath(entry.RemoteId.Value);

        // Reconcile against any prior install of the same RemoteId.
        var previous = LoadManifestFile(path);
        if (previous is not null)
        {
            var keep = entry.Files.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var stale = new List<string>();
            foreach (var rel in previous.Files)
            {
                if (string.IsNullOrWhiteSpace(rel) || keep.Contains(rel))
                    continue;

                DeleteOverlayFile(rel);
                stale.Add(rel);
            }

            // Remove directories left empty by the stale-file deletions, so a version
            // upgrade does not leave the old layout's empty folders behind.
            PruneEmptyAncestors(stale);
        }

        Directory.CreateDirectory(_manifestsDir);
        var json = JsonSerializer.Serialize(entry, JsonOptions);
        File.WriteAllText(path, json);
    }

    /// <summary>
    /// Remove a mod by RemoteId: delete every file its manifest recorded, then delete the
    /// manifest itself. Returns false if no manifest exists for <paramref name="remoteId"/>.
    /// </summary>
    public bool Remove(int remoteId)
    {
        if (string.IsNullOrWhiteSpace(_manifestsDir))
            return false;

        var path = ManifestPath(remoteId);
        var mod = LoadManifestFile(path);
        if (mod is null)
            return false;

        foreach (var rel in mod.Files)
            DeleteOverlayFile(rel);

        // An install records only the files it wrote, never the directories it created
        // (those come into being as parents of file extraction). Deleting the files
        // leaves those directories behind; prune the now-empty ones so uninstall is
        // complete instead of littering the overlay (and the virtual drive) with the
        // mod's empty folder skeleton.
        PruneEmptyAncestors(mod.Files);

        try { File.Delete(path); }
        catch { /* best effort */ }

        return true;
    }

    public bool IsInstalled(int remoteId)
    {
        if (string.IsNullOrWhiteSpace(_manifestsDir))
            return false;

        var path = ManifestPath(remoteId);
        return File.Exists(path);
    }

    private string ManifestPath(int remoteId) =>
        Path.Combine(_manifestsDir, $"{remoteId}.json");

    private void DeleteOverlayFile(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || string.IsNullOrWhiteSpace(_overlayRoot))
            return;

        var full = Path.Combine(_overlayRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            if (File.Exists(full))
                File.Delete(full);
        }
        catch
        {
            // best effort: a locked file should not block the rest of the uninstall.
        }
    }

    /// <summary>
    /// Removes directories left empty by deleting the given overlay-relative files.
    /// Walks each file's ancestors deepest-first and deletes a directory only when
    /// it is empty, stopping at the first non-empty (or missing) ancestor. The
    /// overlay root itself and the <c>.unmod-manifests</c> directory are never
    /// removed, even if empty -- they are loader-owned, not part of a mod's layout.
    /// </summary>
    private void PruneEmptyAncestors(IEnumerable<string> relativePaths)
    {
        if (string.IsNullOrWhiteSpace(_overlayRoot))
            return;

        var overlayRoot = Path.GetFullPath(_overlayRoot)
            .TrimEnd(Path.DirectorySeparatorChar);
        var manifestsRoot = Path.GetFullPath(_manifestsDir)
            .TrimEnd(Path.DirectorySeparatorChar);

        // Collect each ancestor once; a directory shared by several files is tested once.
        var ancestors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rel in relativePaths)
        {
            if (string.IsNullOrWhiteSpace(rel))
                continue;

            var full = Path.GetFullPath(Path.Combine(_overlayRoot,
                rel.Replace('/', Path.DirectorySeparatorChar)));
            for (var dir = Path.GetDirectoryName(full);
                 !string.IsNullOrEmpty(dir);
                 dir = Path.GetDirectoryName(dir))
            {
                // Stop above the overlay: never touch the overlay root or its parents.
                if (!dir.StartsWith(overlayRoot, StringComparison.OrdinalIgnoreCase))
                    break;

                if (dir.Equals(manifestsRoot, StringComparison.OrdinalIgnoreCase))
                    continue;

                ancestors.Add(dir);
            }
        }

        // Deepest first: deleting a leaf may empty its parent, which we then reach next.
        // Sort by descending path length so a directory is processed after its children.
        // (Depth correlates with path length here; exact tree order is not required, only
        // that children precede parents. Enumerable.OrderBy is used explicitly because an
        // async-extension shim in scope otherwise hijacks overload resolution.)
        var ordered = System.Linq.Enumerable.OrderBy<string, int>(ancestors, d => d.Length);
        foreach (var dir in System.Linq.Enumerable.Reverse(ordered))
        {
            try
            {
                if (!Directory.Exists(dir))
                    continue;

                // A non-empty directory is shared with another mod (or with copy-up writes
                // from the live VFS); leave it in place rather than guessing ownership.
                if (Directory.EnumerateFileSystemEntries(dir).Any())
                    continue;

                Directory.Delete(dir, recursive: false);
            }
            catch
            {
                // best effort: a locked/shared directory should not block the rest.
            }
        }
    }

    private static InstalledMod? LoadManifestFile(string path)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            return JsonSerializer.Deserialize<InstalledMod>(File.ReadAllText(path), JsonOptions);
        }
        catch
        {
            return null;
        }
    }
}

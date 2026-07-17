using System.IO.Compression;
using System.Text.Json;

using Avalonia.Controls;
using Avalonia.Platform.Storage;
using UnturnedModLoader.Models;
using UnturnedModLoader.Models.Api;
using UnturnedModLoader.Services.Api;

namespace UnturnedModLoader.Services;

public sealed class ModDownloadService
{
    private readonly InstalledModsService? _installedMods;

    public ModDownloadService() : this(null) { }

    public ModDownloadService(InstalledModsService? installedMods)
    {
        _installedMods = installedMods;
    }

    /// <param name="overlayRoot">
    /// Profile overlay root (e.g. profiles\{id}\overlay) -- the VFS upper layer that maps 1:1
    /// to the game root. The package is extracted here so its internal layout (root files,
    /// <c>BepInEx\</c>, <c>Modules\X\</c>, …) lands at the correct game-relative paths.
    /// Null -> save-to-disk dialog instead of installing into a profile.
    /// </param>
    /// <param name="progress">
    /// Optional callback for dependency install progress messages.
    /// </param>
    public async Task<ModInstallResult> DownloadAndInstallAsync(
        IModsApiClient modsApi,
        int modId,
        string? overlayRoot,
        Window owner,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var installingToProfile = !string.IsNullOrWhiteSpace(overlayRoot);

        // When installing to a profile, resolve and install dependencies first
        RemoteModDetail? modDetail = null;
        if (installingToProfile)
        {
            var detailResult = await modsApi.GetModAsync(modId, cancellationToken);
            // Detail fetch is best-effort for metadata; a failure here should not block the
            // download itself, only the manifest metadata.
            modDetail = detailResult.Success ? detailResult.Mod : null;

            if (modDetail?.Dependencies.Count > 0)
            {
                var depResult = await InstallDependenciesAsync(
                    modsApi, modDetail.Dependencies, overlayRoot!, progress,
                    new HashSet<int> { modId }, cancellationToken);
                if (!depResult.Success)
                    return depResult;
            }
        }

        var download = await modsApi.DownloadModFileAsync(modId, cancellationToken);
        if (!download.Success || download.Content is null || string.IsNullOrWhiteSpace(download.FileName))
            return ModInstallResult.Failed(download.Error ?? "Download failed.");

        if (installingToProfile)
        {
            try
            {
                Directory.CreateDirectory(overlayRoot!);
                await Task.Run(() => InstallAndRecord(
                    overlayRoot!, download.FileName!, download.Content!, modId, modDetail, progress),
                    cancellationToken);

                return ModInstallResult.Installed(overlayRoot!);
            }
            catch (Exception ex)
            {
                return ModInstallResult.Failed(ex.Message);
            }
        }

        var savedPath = await SaveFileAsync(owner, download.FileName, download.Content);
        return savedPath is null
            ? ModInstallResult.CanceledByUser()
            : ModInstallResult.Saved(savedPath);
    }

    /// <summary>
    /// Extract the package directly into the profile overlay root (which the VFS presents as
    /// the game root), collect every file written, and record a per-RemoteId manifest so the
    /// installed list can show remote metadata and uninstall can remove exactly these files.
    /// </summary>
    private void InstallAndRecord(
        string overlayRoot,
        string fileName,
        byte[] content,
        int modId,
        RemoteModDetail? mod,
        Action<string>? progress)
    {
        var locale = LocalizationService.CurrentLocaleCode;
        var modTitle = mod is not null
            ? LocalizedContent.Pick(mod.Title, locale)
            : Path.GetFileNameWithoutExtension(fileName);
        progress?.Invoke($"Installing {modTitle}…");

        var files = ExtractToOverlay(overlayRoot, fileName, content);

        if (_installedMods is null)
            return;

        var title = modTitle;
        if (string.IsNullOrWhiteSpace(title))
            title = Path.GetFileNameWithoutExtension(fileName);

        _installedMods.RecordInstall(new InstalledMod
        {
            RemoteId = modId,
            Title = title,
            Author = mod?.AuthorName,
            Version = mod?.Version,
            Category = mod?.Category,
            Description = mod is not null ? LocalizedContent.Pick(mod.Description, locale) : null,
            CoverUrl = mod?.CoverUrl,
            InstalledAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Files = files,
        });
    }

    private async Task<ModInstallResult> InstallDependenciesAsync(
        IModsApiClient modsApi,
        List<RemoteModDependency> dependencies,
        string overlayRoot,
        Action<string>? progress,
        HashSet<int> visited,
        CancellationToken cancellationToken)
    {
        foreach (var dep in dependencies)
        {
            if (visited.Contains(dep.Id))
                continue;
            visited.Add(dep.Id);

            var depTitle = LocalizedContent.Pick(dep.Title, LocalizationService.CurrentLocaleCode);
            if (string.IsNullOrWhiteSpace(depTitle))
                depTitle = $"#{dep.Id}";
            progress?.Invoke($"Downloading dependency: {depTitle}");

            // Recursively fetch and install transitive dependencies
            var depDetail = await modsApi.GetModAsync(dep.Id, cancellationToken);
            if (depDetail.Success && depDetail.Mod?.Dependencies.Count > 0)
            {
                var subResult = await InstallDependenciesAsync(
                    modsApi, depDetail.Mod.Dependencies, overlayRoot, progress,
                    visited, cancellationToken);
                if (!subResult.Success)
                    return subResult;
            }

            // Download and install the dependency itself
            var download = await modsApi.DownloadModFileAsync(dep.Id, cancellationToken);
            if (!download.Success || download.Content is null || string.IsNullOrWhiteSpace(download.FileName))
                return ModInstallResult.Failed($"Failed to download dependency: {depTitle}");

            try
            {
                Directory.CreateDirectory(overlayRoot);
                InstallAndRecord(overlayRoot, download.FileName, download.Content,
                    dep.Id, depDetail.Success ? depDetail.Mod : null, progress);
            }
            catch (Exception ex)
            {
                return ModInstallResult.Failed($"Failed to install dependency {depTitle}: {ex.Message}");
            }
        }

        return ModInstallResult.Installed(overlayRoot);
    }

    /// <summary>
    /// Extract a package into <paramref name="overlayRoot"/> and return every file written as
    /// an overlay-relative path (forward-slash separated). Zips are walked entry-by-entry with
    /// zip-slip protection; a bare non-zip file is written under its own name.
    /// </summary>
    private static List<string> ExtractToOverlay(string overlayRoot, string fileName, byte[] content)
    {
        var safeName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeName))
            throw new InvalidOperationException("Invalid file name.");

        var extension = Path.GetExtension(safeName).ToLowerInvariant();
        if (extension == ".zip")
            return ExtractZipSafely(content, overlayRoot);

        var destination = GetUniqueDestinationPath(overlayRoot, safeName);
        File.WriteAllBytes(destination, content);
        return [Path.GetRelativePath(overlayRoot, destination).Replace('\\', '/')];
    }

    private static List<string> ExtractZipSafely(byte[] content, string destinationFolder)
    {
        var written = new List<string>();
        using var stream = new MemoryStream(content);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        var root = Path.GetFullPath(destinationFolder);
        if (!root.EndsWith(Path.DirectorySeparatorChar))
            root += Path.DirectorySeparatorChar;

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
                continue;

            var destination = Path.GetFullPath(Path.Combine(destinationFolder, entry.FullName));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Archive contains invalid paths.");

            var directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            entry.ExtractToFile(destination, overwrite: true);
            written.Add(Path.GetRelativePath(destinationFolder, destination).Replace('\\', '/'));
        }

        return written;
    }

    private static string GetUniqueDestinationPath(string directory, string fileName)
    {
        var destination = Path.Combine(directory, fileName);
        if (!File.Exists(destination))
            return destination;

        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var index = 1;

        while (true)
        {
            var candidate = Path.Combine(directory, $"{baseName}_{index}{extension}");
            if (!File.Exists(candidate))
                return candidate;

            index++;
        }
    }

    private static async Task<string?> SaveFileAsync(Window owner, string fileName, byte[] content)
    {
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = fileName,
            SuggestedFileName = fileName,
            DefaultExtension = Path.GetExtension(fileName).TrimStart('.'),
        });

        if (file is null)
            return null;

        await using var stream = await file.OpenWriteAsync();
        await stream.WriteAsync(content);
        return file.Path.LocalPath;
    }
}

public sealed class ModInstallResult
{
    public bool Success { get; init; }
    public bool Cancelled { get; init; }
    public bool InstalledToGame { get; init; }
    public string? Path { get; init; }
    public string? Error { get; init; }

    public static ModInstallResult Installed(string modulesFolder) =>
        new() { Success = true, InstalledToGame = true, Path = modulesFolder };

    public static ModInstallResult Saved(string savedPath) =>
        new() { Success = true, Path = savedPath };

    public static ModInstallResult Failed(string error) =>
        new() { Error = error };

    public static ModInstallResult CanceledByUser() =>
        new() { Cancelled = true };
}

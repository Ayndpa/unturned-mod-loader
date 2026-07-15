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
    private readonly ModScriptService _scriptService;
    private readonly InstalledModsService? _installedMods;

    public ModDownloadService() : this(new ModScriptService(), null) { }

    public ModDownloadService(ModScriptService scriptService, InstalledModsService? installedMods)
    {
        _scriptService = scriptService;
        _installedMods = installedMods;
    }

    /// <param name="modulesFolder">
    /// Profile overlay Modules folder (e.g. profiles\{id}\overlay\Modules). Null -> save dialog.
    /// </param>
    /// <param name="progress">
    /// Optional callback for dependency install progress messages.
    /// </param>
    public async Task<ModInstallResult> DownloadAndInstallAsync(
        IModsApiClient modsApi,
        int modId,
        string? modulesFolder,
        Window owner,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var installContext = string.IsNullOrWhiteSpace(modulesFolder)
            ? null
            : BuildInstallContext(modulesFolder!);

        // When installing to a profile, resolve and install dependencies first
        RemoteModDetail? modDetail = null;
        if (installContext is not null)
        {
            var detailResult = await modsApi.GetModAsync(modId, cancellationToken);
            // Detail fetch is best-effort for metadata; a failure here should not block the
            // download itself, only the manifest metadata.
            modDetail = detailResult.Success ? detailResult.Mod : null;

            if (modDetail?.Dependencies.Count > 0)
            {
                var depResult = await InstallDependenciesAsync(
                    modsApi, modDetail.Dependencies, modulesFolder!, progress,
                    new HashSet<int> { modId }, cancellationToken);
                if (!depResult.Success)
                    return depResult;
            }
        }

        var download = await modsApi.DownloadModFileAsync(modId, cancellationToken);
        if (!download.Success || download.Content is null || string.IsNullOrWhiteSpace(download.FileName))
            return ModInstallResult.Failed(download.Error ?? "Download failed.");

        if (installContext is not null)
        {
            try
            {
                Directory.CreateDirectory(modulesFolder!);
                var staged = await Task.Run(() => InstallToProfile(
                    installContext, modId, modDetail, download.FileName!, download.Content!, progress, cancellationToken),
                    cancellationToken);

                return staged.Success
                    ? ModInstallResult.Installed(modulesFolder!)
                    : staged;
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
    /// Derive the install context (overlay root, staging root, game-running state) from the
    /// profile Modules folder path. <see cref="InstallContext.GamePath"/> is left empty because
    /// scripted installs stage into the profile overlay only.
    /// </summary>
    private static InstallContext BuildInstallContext(string modulesFolder)
    {
        var modulesInfo = new DirectoryInfo(modulesFolder);
        var overlayDir = modulesInfo.Parent?.FullName ?? modulesFolder;
        // profiles\{id}\scripts - per-mod subfolders created on install.
        var stagingRoot = Path.GetFullPath(Path.Combine(overlayDir, "..", "scripts"));

        return new InstallContext
        {
            Action = InstallAction.Install,
            ModId = 0,
            ModTitle = "",
            ModVersion = "",
            GamePath = "",
            OverlayDir = overlayDir,
            StagingDir = stagingRoot,
            GameRunning = GameProcessService.IsRunning(),
        };
    }

    /// <summary>
    /// Extract the mod payload into a per-mod staging dir, run the install script if present
    /// (else fall back to copying into overlay\Modules), and record the manifest entry.
    /// </summary>
    private ModInstallResult InstallToProfile(
        InstallContext baseContext,
        int modId,
        RemoteModDetail? mod,
        string fileName,
        byte[] content,
        Action<string>? progress,
        CancellationToken cancellationToken)
    {
        var stagingDir = Path.Combine(baseContext.StagingDir, modId.ToString());
        Directory.CreateDirectory(stagingDir);

        var context = baseContext with
        {
            ModId = modId,
            ModTitle = mod?.Title ?? fileName,
            ModVersion = mod?.Version ?? "1.0.0",
            StagingDir = stagingDir,
        };

        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (extension == ".zip")
            ExtractZipSafely(content, stagingDir);
        else
            File.WriteAllBytes(GetUniqueDestinationPath(stagingDir, Path.GetFileName(fileName)), content);

        var installScript = ModScriptService.FindScript(stagingDir, InstallAction.Install);
        if (installScript is null)
        {
            // No script -> legacy default: copy staged files straight into overlay\Modules.
            progress?.Invoke($"Installing {context.ModTitle}…");
            CopyToModules(stagingDir, context.ModulesDir);
            RecordManifestEntry(context, mod, stagingDir, installedFiles: null);
            return ModInstallResult.Installed(context.ModulesDir);
        }

        progress?.Invoke($"Running install script for {context.ModTitle}…");

        // Snapshot overlay before the script runs so we can diff exactly what it wrote.
        var before = ModScriptService.SnapshotOverlay(context.OverlayDir);
        var run = _scriptService.Run(installScript, context, cancellationToken);
        if (!run.Success)
            return ModInstallResult.Failed(run.Error ?? "Install script failed.");

        var after = ModScriptService.SnapshotOverlay(context.OverlayDir);
        var installedFiles = after.Keys
            .Where(k => !before.ContainsKey(k))
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();

        RecordManifestEntry(context, mod, stagingDir, installedFiles);
        return ModInstallResult.Installed(context.ModulesDir);
    }

    /// <summary>Copy staged files into the overlay Modules folder (default no-script path).</summary>
    private static void CopyToModules(string stagingDir, string modulesDir)
    {
        Directory.CreateDirectory(modulesDir);
        foreach (var entry in Directory.EnumerateFileSystemEntries(stagingDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(stagingDir, entry);
            var dest = Path.Combine(modulesDir, rel);
            if (Directory.Exists(entry))
                Directory.CreateDirectory(dest);
            else if (File.Exists(entry))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(entry, dest, overwrite: true);
            }
        }
    }

    private void RecordManifestEntry(
        InstallContext context,
        RemoteModDetail? mod,
        string stagingDir,
        List<string>? installedFiles)
    {
        if (_installedMods is null)
            return;

        var profileRoot = Path.GetDirectoryName(context.OverlayDir)!; // profiles\{id}
        var entry = new InstalledMod
        {
            RemoteId = context.ModId,
            Kind = LocalModKind.Scripted,
            RelativePath = mod?.Title ?? context.ModTitle,
            Title = mod?.Title ?? context.ModTitle,
            Author = mod?.AuthorName,
            Version = mod?.Version,
            Category = mod?.Category,
            Description = mod?.Description,
            CoverUrl = mod?.CoverUrl,
            IsEnabled = true,
            InstalledAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            InstalledFiles = installedFiles ?? [],
            StagingDir = Path.GetRelativePath(profileRoot, stagingDir),
        };

        _installedMods.UpsertScripted(entry);
    }

    private async Task<ModInstallResult> InstallDependenciesAsync(
        IModsApiClient modsApi,
        List<RemoteModDependency> dependencies,
        string modulesFolder,
        Action<string>? progress,
        HashSet<int> visited,
        CancellationToken cancellationToken)
    {
        foreach (var dep in dependencies)
        {
            if (visited.Contains(dep.Id))
                continue;
            visited.Add(dep.Id);

            progress?.Invoke($"Downloading dependency: {dep.Title}");

            // Recursively fetch and install transitive dependencies
            var depDetail = await modsApi.GetModAsync(dep.Id, cancellationToken);
            if (depDetail.Success && depDetail.Mod?.Dependencies.Count > 0)
            {
                var subResult = await InstallDependenciesAsync(
                    modsApi, depDetail.Mod.Dependencies, modulesFolder, progress,
                    visited, cancellationToken);
                if (!subResult.Success)
                    return subResult;
            }

            // Download and install the dependency itself
            var download = await modsApi.DownloadModFileAsync(dep.Id, cancellationToken);
            if (!download.Success || download.Content is null || string.IsNullOrWhiteSpace(download.FileName))
                return ModInstallResult.Failed($"Failed to download dependency: {dep.Title}");

            try
            {
                Directory.CreateDirectory(modulesFolder);
                InstallToModules(modulesFolder, download.FileName, download.Content);
            }
            catch (Exception ex)
            {
                return ModInstallResult.Failed($"Failed to install dependency {dep.Title}: {ex.Message}");
            }
        }

        return ModInstallResult.Installed(modulesFolder);
    }

    private static void InstallToModules(string modulesFolder, string fileName, byte[] content)
    {
        var safeName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeName))
            throw new InvalidOperationException("Invalid file name.");

        var extension = Path.GetExtension(safeName).ToLowerInvariant();
        if (extension == ".zip")
        {
            ExtractZipSafely(content, modulesFolder);
            return;
        }

        var destination = GetUniqueDestinationPath(modulesFolder, safeName);
        File.WriteAllBytes(destination, content);
    }

    private static void ExtractZipSafely(byte[] content, string destinationFolder)
    {
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
        }
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

using System.IO.Compression;

using Avalonia.Controls;
using Avalonia.Platform.Storage;
using UnturnedModLoader.Models.Api;
using UnturnedModLoader.Services.Api;

namespace UnturnedModLoader.Services;

public sealed class ModDownloadService
{
    /// <param name="modulesFolder">
    /// Profile overlay Modules folder (e.g. profiles\{id}\overlay\Modules). Null → save dialog.
    /// </param>
    public async Task<ModInstallResult> DownloadAndInstallAsync(
        IModsApiClient modsApi,
        int modId,
        string? modulesFolder,
        Window owner,
        CancellationToken cancellationToken = default)
    {
        var download = await modsApi.DownloadModFileAsync(modId, cancellationToken);
        if (!download.Success || download.Content is null || string.IsNullOrWhiteSpace(download.FileName))
            return ModInstallResult.Failed(download.Error ?? "Download failed.");

        if (!string.IsNullOrWhiteSpace(modulesFolder))
        {
            try
            {
                Directory.CreateDirectory(modulesFolder);
                InstallToModules(modulesFolder, download.FileName, download.Content);
                return ModInstallResult.Installed(modulesFolder);
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

    private static void ExtractZipSafely(byte[] content, string modulesFolder)
    {
        using var stream = new MemoryStream(content);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        var root = Path.GetFullPath(modulesFolder);
        if (!root.EndsWith(Path.DirectorySeparatorChar))
            root += Path.DirectorySeparatorChar;

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
                continue;

            var destination = Path.GetFullPath(Path.Combine(modulesFolder, entry.FullName));
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

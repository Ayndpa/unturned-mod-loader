using System.Text.Json;
using UnturnedModLoader.Models;

namespace UnturnedModLoader.Services;

public class InstalledModsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _manifestPath;
    private InstalledModsManifest _manifest = new();

    public InstalledModsService()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "UnturnedModLoader");
        Directory.CreateDirectory(dir);
        _manifestPath = Path.Combine(dir, "installed-mods.json");
        _manifest = LoadManifest();
    }

    public static string GetModsFolder(string gamePath) =>
        Path.Combine(gamePath, "Modules");

    public IReadOnlyList<InstalledMod> GetAll() => _manifest.Mods;

    public void Reload() => _manifest = LoadManifest();

    public void Save()
    {
        var json = JsonSerializer.Serialize(_manifest, JsonOptions);
        File.WriteAllText(_manifestPath, json);
    }

    public void SyncWithFolder(string gamePath)
    {
        if (!GamePathValidator.IsValid(gamePath))
            return;

        var folder = GetModsFolder(gamePath);
        Directory.CreateDirectory(folder);

        var filesOnDisk = Directory
            .EnumerateFiles(folder)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        _manifest.Mods.RemoveAll(mod => !filesOnDisk.Contains(mod.FileName));

        var tracked = _manifest.Mods
            .Select(m => m.FileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var fileName in filesOnDisk)
        {
            if (tracked.Contains(fileName))
                continue;

            _manifest.Mods.Add(new InstalledMod
            {
                Title = Path.GetFileNameWithoutExtension(fileName),
                FileName = fileName,
                IsEnabled = true,
                InstalledAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            });
        }

        Save();
    }

    public bool Remove(string fileName, string gamePath)
    {
        var mod = _manifest.Mods.FirstOrDefault(m =>
            string.Equals(m.FileName, fileName, StringComparison.OrdinalIgnoreCase));

        if (mod is null)
            return false;

        if (GamePathValidator.IsValid(gamePath))
        {
            var filePath = Path.Combine(GetModsFolder(gamePath), mod.FileName);
            if (File.Exists(filePath))
                File.Delete(filePath);
        }

        _manifest.Mods.Remove(mod);
        Save();
        return true;
    }

    public void SetEnabled(string fileName, bool enabled)
    {
        var mod = _manifest.Mods.FirstOrDefault(m =>
            string.Equals(m.FileName, fileName, StringComparison.OrdinalIgnoreCase));

        if (mod is null)
            return;

        mod.IsEnabled = enabled;
        Save();
    }

    public void OpenModsFolder(string gamePath)
    {
        if (!GamePathValidator.IsValid(gamePath))
            return;

        var folder = GetModsFolder(gamePath);
        Directory.CreateDirectory(folder);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = folder,
            UseShellExecute = true,
        });
    }

    private InstalledModsManifest LoadManifest()
    {
        if (!File.Exists(_manifestPath))
            return new InstalledModsManifest();

        try
        {
            var json = File.ReadAllText(_manifestPath);
            return JsonSerializer.Deserialize<InstalledModsManifest>(json, JsonOptions)
                   ?? new InstalledModsManifest();
        }
        catch
        {
            return new InstalledModsManifest();
        }
    }
}
using System.Text.Json;
using UnturnedModLoader.Models;

namespace UnturnedModLoader.Services;

public class InstalledModsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly LocalModuleParser _parser = new();
    private readonly string _appDataDir;
    private readonly string _manifestPath;
    private readonly string _quarantineDir;
    private InstalledModsManifest _manifest = new();

    public InstalledModsService()
    {
        _appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "UnturnedModLoader");
        Directory.CreateDirectory(_appDataDir);
        _manifestPath = Path.Combine(_appDataDir, "installed-mods.json");
        _quarantineDir = Path.Combine(_appDataDir, "quarantine");
        _manifest = LoadManifest();
    }

    public static string GetModsFolder(string gamePath) =>
        Path.Combine(gamePath, "Modules");

    public IReadOnlyList<InstalledMod> GetAll() => _manifest.Mods;

    public void Reload() => _manifest = LoadManifest();

    public IReadOnlyList<InstalledMod> ScanAndMerge(string gamePath)
    {
        if (!GamePathValidator.IsValid(gamePath))
            return [];

        MigrateQuarantineToDisabled(gamePath);

        var parsedMods = _parser.Scan(gamePath);
        var merged = new List<InstalledMod>();
        var overlayByPath = _manifest.Mods
            .Where(mod => !string.IsNullOrWhiteSpace(mod.RelativePath))
            .ToDictionary(mod => mod.RelativePath, StringComparer.OrdinalIgnoreCase);

        foreach (var parsed in parsedMods)
        {
            overlayByPath.TryGetValue(parsed.RelativePath, out var overlay);
            merged.Add(BuildInstalledMod(parsed, overlay, gamePath, parsed.IsEnabled));
        }

        _manifest.Mods = merged;
        Save();
        return merged;
    }

    public bool Remove(string relativePath, string gamePath)
    {
        var mod = _manifest.Mods.FirstOrDefault(m =>
            string.Equals(m.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));

        if (mod is null)
            return false;

        if (GamePathValidator.IsValid(gamePath))
        {
            if (mod.Kind == LocalModKind.Module)
            {
                if (!string.IsNullOrWhiteSpace(mod.ModuleFilePath) && File.Exists(mod.ModuleFilePath))
                {
                    var directory = Path.GetDirectoryName(mod.ModuleFilePath);
                    if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
                        Directory.Delete(directory, recursive: true);
                }
            }
            else
            {
                var modsFolder = GetModsFolder(gamePath);
                var activePath = DllDisableHelper.GetActivePath(modsFolder, mod.RelativePath);
                var disabledPath = DllDisableHelper.GetDisabledPath(modsFolder, mod.RelativePath);

                if (File.Exists(activePath))
                    File.Delete(activePath);

                if (File.Exists(disabledPath))
                    File.Delete(disabledPath);
            }
        }

        _manifest.Mods.Remove(mod);
        Save();
        return true;
    }

    public bool SetEnabled(string relativePath, string gamePath, bool enabled)
    {
        var mod = _manifest.Mods.FirstOrDefault(m =>
            string.Equals(m.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));

        if (mod is null || !GamePathValidator.IsValid(gamePath))
            return false;

        if (!ApplyEnabledState(mod, gamePath, enabled))
            return false;

        mod.IsEnabled = enabled;
        Save();
        return true;
    }

    public bool SetEnabledMany(IEnumerable<string> relativePaths, string gamePath, bool enabled)
    {
        var paths = relativePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (paths.Count == 0 || !GamePathValidator.IsValid(gamePath))
            return false;

        var changed = new List<InstalledMod>();
        foreach (var relativePath in paths)
        {
            var mod = _manifest.Mods.FirstOrDefault(m =>
                string.Equals(m.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));

            if (mod is null)
                return false;

            if (!ApplyEnabledState(mod, gamePath, enabled))
            {
                foreach (var applied in changed)
                    ApplyEnabledState(applied, gamePath, !enabled);

                return false;
            }

            mod.IsEnabled = enabled;
            changed.Add(mod);
        }

        Save();
        return true;
    }

    public void OpenModsFolder(string gamePath)
    {
        if (!GamePathValidator.IsValid(gamePath))
            return;

        var folder = GetModsFolder(gamePath);
        Directory.CreateDirectory(folder);
        OpenFolderInExplorer(folder);
    }

    public void OpenGameFolder(string gamePath)
    {
        if (!GamePathValidator.IsValid(gamePath))
            return;

        OpenFolderInExplorer(gamePath);
    }

    private static void OpenFolderInExplorer(string folder) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = folder,
            UseShellExecute = true,
        });

    public void Save()
    {
        var json = JsonSerializer.Serialize(_manifest, JsonOptions);
        File.WriteAllText(_manifestPath, json);
    }

    private static bool ApplyEnabledState(InstalledMod mod, string gamePath, bool enabled)
    {
        if (mod.Kind == LocalModKind.Module)
        {
            if (string.IsNullOrWhiteSpace(mod.ModuleFilePath))
                return false;

            if (enabled)
            {
                if (!LocalModuleParser.TrySetAssembliesEnabled(mod.ModuleFilePath, enabled: true))
                    return false;

                if (!LocalModuleParser.TryWriteEnabled(mod.ModuleFilePath, isEnabled: true))
                {
                    LocalModuleParser.TrySetAssembliesEnabled(mod.ModuleFilePath, enabled: false);
                    return false;
                }

                return true;
            }

            if (!LocalModuleParser.TryWriteEnabled(mod.ModuleFilePath, isEnabled: false))
                return false;

            if (!LocalModuleParser.TrySetAssembliesEnabled(mod.ModuleFilePath, enabled: false))
            {
                LocalModuleParser.TryWriteEnabled(mod.ModuleFilePath, isEnabled: true);
                return false;
            }

            return true;
        }

        return DllDisableHelper.TrySetEnabled(GetModsFolder(gamePath), mod.RelativePath, enabled);
    }

    private void MigrateQuarantineToDisabled(string gamePath)
    {
        if (!Directory.Exists(_quarantineDir))
            return;

        var modsFolder = GetModsFolder(gamePath);
        Directory.CreateDirectory(modsFolder);

        foreach (var quarantinedPath in Directory.EnumerateFiles(_quarantineDir, "*.dll", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(_quarantineDir, quarantinedPath);
            var disabledPath = DllDisableHelper.GetDisabledPath(modsFolder, relativePath);

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(disabledPath)!);
                if (File.Exists(disabledPath))
                    File.Delete(quarantinedPath);
                else
                    File.Move(quarantinedPath, disabledPath);
            }
            catch
            {
                // Best-effort migration; leave remaining files in quarantine.
            }
        }
    }

    private static string GetDefaultCategory(LocalModKind kind) =>
        kind == LocalModKind.Module ? "module" : "dll";

    private InstalledMod BuildInstalledMod(
        ParsedLocalMod parsed,
        InstalledMod? overlay,
        string gamePath,
        bool isEnabled)
    {
        var sourcePath = parsed.Kind == LocalModKind.Module && !string.IsNullOrWhiteSpace(parsed.ModuleFilePath)
            ? parsed.ModuleFilePath
            : Path.Combine(GetModsFolder(gamePath), parsed.RelativePath);

        return new InstalledMod
        {
            Kind = parsed.Kind,
            RelativePath = parsed.RelativePath,
            Title = parsed.Title,
            ModuleName = parsed.ModuleName,
            Version = parsed.Version,
            ModuleFilePath = parsed.ModuleFilePath,
            DirectoryPath = parsed.DirectoryPath,
            LocalIconPath = parsed.LocalIconPath,
            DependencyNames = parsed.DependencyNames.ToList(),
            Dependencies = parsed.Dependencies.ToList(),
            Assemblies = parsed.Assemblies.ToList(),
            IsEnabled = isEnabled,
            RemoteId = overlay?.RemoteId,
            Author = overlay?.Author,
            Category = overlay?.Category ?? GetDefaultCategory(parsed.Kind),
            Description = overlay?.Description ?? BuildDescription(parsed),
            CoverUrl = overlay?.CoverUrl,
            InstalledAt = overlay?.InstalledAt
                ?? new DateTimeOffset(File.Exists(sourcePath)
                    ? File.GetCreationTimeUtc(sourcePath)
                    : Directory.GetCreationTimeUtc(parsed.DirectoryPath)).ToUnixTimeSeconds(),
        };
    }

    private static string BuildDescription(ParsedLocalMod parsed)
    {
        if (parsed.Dependencies.Count == 0 && parsed.Assemblies.Count == 0)
            return "";

        var parts = new List<string>();
        if (parsed.Assemblies.Count > 0)
            parts.Add($"{parsed.Assemblies.Count} assemblies");
        if (parsed.Dependencies.Count > 0)
            parts.Add($"{parsed.Dependencies.Count} dependencies");

        return string.Join(" · ", parts);
    }

    private InstalledModsManifest LoadManifest()
    {
        if (!File.Exists(_manifestPath))
            return new InstalledModsManifest();

        try
        {
            var json = File.ReadAllText(_manifestPath);
            var manifest = JsonSerializer.Deserialize<InstalledModsManifest>(json, JsonOptions)
                           ?? new InstalledModsManifest();

            foreach (var mod in manifest.Mods)
            {
                if (string.IsNullOrWhiteSpace(mod.RelativePath) && !string.IsNullOrWhiteSpace(mod.FileName))
                    mod.RelativePath = mod.FileName;
            }

            return manifest;
        }
        catch
        {
            return new InstalledModsManifest();
        }
    }
}
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
    private string _profileId = GameProfile.VanillaId;
    private bool _isVanilla = true;
    private string _manifestPath = "";
    private string _modulesRoot = "";
    private string _quarantineDir = "";
    private InstalledModsManifest _manifest = new();

    public InstalledModsService()
    {
        AppPaths.EnsureAppData();
        UseProfile(GameProfile.VanillaId, isVanilla: true);
    }

    /// <summary>Legacy alias for game Modules path (mount target).</summary>
    public static string GetModsFolder(string gamePath) => AppPaths.GameModulesFolder(gamePath);

    public string ActiveProfileId => _profileId;
    public bool IsVanilla => _isVanilla;
    public string ModulesRoot => _modulesRoot;

    public void UseProfile(string profileId, bool isVanilla)
    {
        _profileId = profileId;
        _isVanilla = isVanilla;

        if (isVanilla)
        {
            _manifestPath = "";
            _modulesRoot = "";
            _quarantineDir = "";
            _manifest = new InstalledModsManifest();
            return;
        }

        AppPaths.EnsureProfileLayout(profileId);
        _manifestPath = AppPaths.ProfileInstalledModsPath(profileId);
        _modulesRoot = AppPaths.ProfileModulesFolder(profileId);
        _quarantineDir = AppPaths.ProfileQuarantineDir(profileId);
        Directory.CreateDirectory(_modulesRoot);
        _manifest = LoadManifest();
    }

    public IReadOnlyList<InstalledMod> GetAll() => _manifest.Mods;

    public void Reload() => _manifest = _isVanilla ? new InstalledModsManifest() : LoadManifest();

    /// <summary>
    /// Scan the active profile's Modules store (not the game path).
    /// <paramref name="gamePath"/> is unused for scanning but kept for call-site compatibility;
    /// validation is based on profile store existence.
    /// </summary>
    public IReadOnlyList<InstalledMod> ScanAndMerge(string? gamePath = null)
    {
        if (_isVanilla)
        {
            _manifest = new InstalledModsManifest();
            return [];
        }

        if (string.IsNullOrWhiteSpace(_modulesRoot))
            return [];

        Directory.CreateDirectory(_modulesRoot);
        MigrateQuarantineToDisabled();

        var parsedMods = _parser.ScanModulesRoot(_modulesRoot);
        var merged = new List<InstalledMod>();
        var overlayByPath = _manifest.Mods
            .Where(mod => !string.IsNullOrWhiteSpace(mod.RelativePath))
            .ToDictionary(mod => mod.RelativePath, StringComparer.OrdinalIgnoreCase);

        foreach (var parsed in parsedMods)
        {
            overlayByPath.TryGetValue(parsed.RelativePath, out var overlay);
            merged.Add(BuildInstalledMod(parsed, overlay, parsed.IsEnabled));
        }

        _manifest.Mods = merged;
        Save();
        return merged;
    }

    public bool Remove(string relativePath, string? gamePath = null)
    {
        if (_isVanilla)
            return false;

        var mod = _manifest.Mods.FirstOrDefault(m =>
            string.Equals(m.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));

        if (mod is null)
            return false;

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
            var activePath = DllDisableHelper.GetActivePath(_modulesRoot, mod.RelativePath);
            var disabledPath = DllDisableHelper.GetDisabledPath(_modulesRoot, mod.RelativePath);

            if (File.Exists(activePath))
                File.Delete(activePath);

            if (File.Exists(disabledPath))
                File.Delete(disabledPath);
        }

        _manifest.Mods.Remove(mod);
        Save();
        return true;
    }

    public bool SetEnabled(string relativePath, string? gamePath, bool enabled)
    {
        var mod = _manifest.Mods.FirstOrDefault(m =>
            string.Equals(m.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));

        if (mod is null || _isVanilla)
            return false;

        if (!ApplyEnabledState(mod, enabled))
            return false;

        mod.IsEnabled = enabled;
        Save();
        return true;
    }

    public bool SetEnabledMany(IEnumerable<string> relativePaths, string? gamePath, bool enabled)
    {
        if (_isVanilla)
            return false;

        var paths = relativePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (paths.Count == 0)
            return false;

        var changed = new List<InstalledMod>();
        foreach (var relativePath in paths)
        {
            var mod = _manifest.Mods.FirstOrDefault(m =>
                string.Equals(m.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));

            if (mod is null)
                return false;

            if (!ApplyEnabledState(mod, enabled))
            {
                foreach (var applied in changed)
                    ApplyEnabledState(applied, !enabled);

                return false;
            }

            mod.IsEnabled = enabled;
            changed.Add(mod);
        }

        Save();
        return true;
    }

    public void Save()
    {
        if (_isVanilla || string.IsNullOrWhiteSpace(_manifestPath))
            return;

        var json = JsonSerializer.Serialize(_manifest, JsonOptions);
        File.WriteAllText(_manifestPath, json);
    }

    private bool ApplyEnabledState(InstalledMod mod, bool enabled)
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

        return DllDisableHelper.TrySetEnabled(_modulesRoot, mod.RelativePath, enabled);
    }

    private void MigrateQuarantineToDisabled()
    {
        if (string.IsNullOrWhiteSpace(_quarantineDir) || !Directory.Exists(_quarantineDir))
            return;

        Directory.CreateDirectory(_modulesRoot);

        foreach (var quarantinedPath in Directory.EnumerateFiles(_quarantineDir, "*.dll", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(_quarantineDir, quarantinedPath);
            var disabledPath = DllDisableHelper.GetDisabledPath(_modulesRoot, relativePath);

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

        // Legacy global quarantine → current profile
        if (Directory.Exists(AppPaths.LegacyQuarantineDir))
        {
            foreach (var quarantinedPath in Directory.EnumerateFiles(AppPaths.LegacyQuarantineDir, "*.dll", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(AppPaths.LegacyQuarantineDir, quarantinedPath);
                var disabledPath = DllDisableHelper.GetDisabledPath(_modulesRoot, relativePath);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(disabledPath)!);
                    if (!File.Exists(disabledPath))
                        File.Move(quarantinedPath, disabledPath);
                    else
                        File.Delete(quarantinedPath);
                }
                catch
                {
                    // ignore
                }
            }
        }
    }

    private static string GetDefaultCategory(LocalModKind kind) =>
        kind == LocalModKind.Module ? "module" : "dll";

    private InstalledMod BuildInstalledMod(
        ParsedLocalMod parsed,
        InstalledMod? overlay,
        bool isEnabled)
    {
        var sourcePath = parsed.Kind == LocalModKind.Module && !string.IsNullOrWhiteSpace(parsed.ModuleFilePath)
            ? parsed.ModuleFilePath
            : Path.Combine(_modulesRoot, parsed.RelativePath);

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
        if (string.IsNullOrWhiteSpace(_manifestPath) || !File.Exists(_manifestPath))
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

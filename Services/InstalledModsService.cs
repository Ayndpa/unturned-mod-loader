using System.Reflection;
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
        Directory.CreateDirectory(_quarantineDir);
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

        var parsedMods = _parser.Scan(gamePath);
        var merged = new List<InstalledMod>();
        var overlayByPath = _manifest.Mods
            .Where(mod => !string.IsNullOrWhiteSpace(mod.RelativePath))
            .ToDictionary(mod => mod.RelativePath, StringComparer.OrdinalIgnoreCase);

        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var parsed in parsedMods)
        {
            overlayByPath.TryGetValue(parsed.RelativePath, out var overlay);
            seenPaths.Add(parsed.RelativePath);

            var isEnabled = parsed.Kind == LocalModKind.Module
                ? parsed.IsEnabled
                : overlay?.IsEnabled ?? !IsQuarantined(parsed.RelativePath);

            merged.Add(BuildInstalledMod(parsed, overlay, gamePath, isEnabled));
        }

        foreach (var quarantined in FindQuarantinedDlls())
        {
            if (seenPaths.Contains(quarantined.RelativePath))
                continue;

            overlayByPath.TryGetValue(quarantined.RelativePath, out var overlay);
            seenPaths.Add(quarantined.RelativePath);
            merged.Add(BuildInstalledMod(quarantined, overlay, gamePath, isEnabled: false));
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
                var activePath = Path.Combine(GetModsFolder(gamePath), mod.RelativePath);
                if (File.Exists(activePath))
                    File.Delete(activePath);

                var quarantinedPath = GetQuarantinePath(mod.RelativePath);
                if (File.Exists(quarantinedPath))
                    File.Delete(quarantinedPath);
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

        if (mod.Kind == LocalModKind.Module)
        {
            if (string.IsNullOrWhiteSpace(mod.ModuleFilePath))
                return false;

            if (!LocalModuleParser.TryWriteEnabled(mod.ModuleFilePath, enabled))
                return false;
        }
        else
        {
            var activePath = Path.Combine(GetModsFolder(gamePath), mod.RelativePath);
            var quarantinedPath = GetQuarantinePath(mod.RelativePath);

            if (enabled)
            {
                if (!File.Exists(quarantinedPath))
                    return mod.IsEnabled;

                Directory.CreateDirectory(Path.GetDirectoryName(activePath)!);
                if (File.Exists(activePath))
                    File.Delete(activePath);

                File.Move(quarantinedPath, activePath);
            }
            else
            {
                if (!File.Exists(activePath))
                    return !mod.IsEnabled;

                Directory.CreateDirectory(Path.GetDirectoryName(quarantinedPath)!);
                if (File.Exists(quarantinedPath))
                    File.Delete(quarantinedPath);

                File.Move(activePath, quarantinedPath);
            }
        }

        mod.IsEnabled = enabled;
        Save();
        return true;
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

    public void Save()
    {
        var json = JsonSerializer.Serialize(_manifest, JsonOptions);
        File.WriteAllText(_manifestPath, json);
    }

    private bool IsQuarantined(string relativePath) =>
        File.Exists(GetQuarantinePath(relativePath));

    private string GetQuarantinePath(string relativePath) =>
        Path.Combine(_quarantineDir, relativePath.Replace('/', Path.DirectorySeparatorChar));

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
            Version = parsed.Version,
            ModuleFilePath = parsed.ModuleFilePath,
            DirectoryPath = parsed.DirectoryPath,
            LocalIconPath = parsed.LocalIconPath,
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

    private List<ParsedLocalMod> FindQuarantinedDlls()
    {
        if (!Directory.Exists(_quarantineDir))
            return [];

        var results = new List<ParsedLocalMod>();

        foreach (var dllPath in Directory.EnumerateFiles(_quarantineDir, "*.dll", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(_quarantineDir, dllPath);
            AssemblyName? assemblyName = null;
            try
            {
                assemblyName = AssemblyName.GetAssemblyName(dllPath);
            }
            catch
            {
                // Keep file-name based metadata when the DLL cannot be inspected.
            }

            var directoryPath = Path.GetDirectoryName(dllPath) ?? _quarantineDir;
            results.Add(new ParsedLocalMod
            {
                Kind = LocalModKind.Dll,
                RelativePath = relativePath,
                Title = assemblyName?.Name ?? Path.GetFileNameWithoutExtension(dllPath),
                Version = assemblyName?.Version?.ToString() ?? "—",
                DirectoryPath = directoryPath,
                LocalIconPath = LocalModuleParser.FindLocalIcon(directoryPath),
            });
        }

        return results;
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
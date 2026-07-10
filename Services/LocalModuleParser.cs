using System.Reflection;
using System.Text.Json;
using UnturnedModLoader.Models;

namespace UnturnedModLoader.Services;

public sealed class LocalModuleParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly HashSet<string> IgnoredDependencyNames =
        new(StringComparer.OrdinalIgnoreCase) { "Framework", "Unturned" };

    public IReadOnlyList<ParsedLocalMod> Scan(string gamePath)
    {
        if (!GamePathValidator.IsValid(gamePath))
            return [];

        var modulesRoot = InstalledModsService.GetModsFolder(gamePath);
        if (!Directory.Exists(modulesRoot))
            return [];

        var referencedDlls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parsedModules = new List<ParsedLocalMod>();

        FindModuleFiles(modulesRoot, modulesRoot, referencedDlls, parsedModules);

        var parsedDlls = FindStandaloneDlls(modulesRoot, modulesRoot, referencedDlls);
        parsedModules.AddRange(parsedDlls);

        return parsedModules
            .OrderBy(mod => mod.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static UnturnedModuleConfig? TryReadConfig(string moduleFilePath)
    {
        try
        {
            var json = File.ReadAllText(moduleFilePath);
            return JsonSerializer.Deserialize<UnturnedModuleConfig>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public static bool TryWriteEnabled(string moduleFilePath, bool isEnabled)
    {
        var config = TryReadConfig(moduleFilePath);
        if (config is null)
            return false;

        config.IsEnabled = isEnabled;

        try
        {
            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions
            {
                WriteIndented = true,
            });
            File.WriteAllText(moduleFilePath, json);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void FindModuleFiles(
        string modulesRoot,
        string currentPath,
        HashSet<string> referencedDlls,
        List<ParsedLocalMod> results)
    {
        foreach (var moduleFile in Directory.EnumerateFiles(currentPath, "*.module"))
        {
            var parsed = TryParseModuleFile(modulesRoot, moduleFile);
            if (parsed is null)
                continue;

            foreach (var assembly in parsed.Assemblies)
            {
                var assemblyPath = Path.Combine(parsed.DirectoryPath, assembly);
                if (File.Exists(assemblyPath))
                    referencedDlls.Add(Path.GetFullPath(assemblyPath));
            }

            results.Add(parsed);
        }

        foreach (var directory in Directory.EnumerateDirectories(currentPath))
            FindModuleFiles(modulesRoot, directory, referencedDlls, results);
    }

    private static ParsedLocalMod? TryParseModuleFile(string modulesRoot, string moduleFilePath)
    {
        var config = TryReadConfig(moduleFilePath);
        if (config is null)
            return null;

        var directoryPath = Path.GetDirectoryName(moduleFilePath) ?? modulesRoot;
        var relativePath = Path.GetRelativePath(modulesRoot, moduleFilePath);
        var title = string.IsNullOrWhiteSpace(config.Name)
            ? Path.GetFileNameWithoutExtension(moduleFilePath)
            : config.Name;

        var dependencies = config.Dependencies
            .Where(dep => !string.IsNullOrWhiteSpace(dep.Name))
            .Where(dep => !IgnoredDependencyNames.Contains(dep.Name))
            .Select(dep => string.IsNullOrWhiteSpace(dep.Version)
                ? dep.Name
                : $"{dep.Name} ({dep.Version})")
            .ToList();

        var assemblies = config.Assemblies
            .Where(assembly => !string.IsNullOrWhiteSpace(assembly.Path))
            .Select(assembly => assembly.Path)
            .ToList();

        return new ParsedLocalMod
        {
            Kind = LocalModKind.Module,
            RelativePath = relativePath,
            Title = title,
            Version = string.IsNullOrWhiteSpace(config.Version) ? "1.0.0.0" : config.Version,
            IsEnabled = config.IsEnabled,
            ModuleFilePath = moduleFilePath,
            DirectoryPath = directoryPath,
            LocalIconPath = FindLocalIcon(directoryPath),
            Dependencies = dependencies,
            Assemblies = assemblies,
        };
    }

    private static List<ParsedLocalMod> FindStandaloneDlls(
        string modulesRoot,
        string currentPath,
        HashSet<string> referencedDlls)
    {
        var results = new List<ParsedLocalMod>();

        foreach (var dllPath in Directory.EnumerateFiles(currentPath, "*.dll"))
        {
            if (referencedDlls.Contains(Path.GetFullPath(dllPath)))
                continue;

            var relativePath = Path.GetRelativePath(modulesRoot, dllPath);
            AssemblyName? assemblyName = null;
            try
            {
                assemblyName = AssemblyName.GetAssemblyName(dllPath);
            }
            catch
            {
                // Keep file-name based metadata when the DLL cannot be inspected.
            }

            var title = assemblyName?.Name ?? Path.GetFileNameWithoutExtension(dllPath);
            var version = assemblyName?.Version?.ToString() ?? "—";
            var directoryPath = Path.GetDirectoryName(dllPath) ?? modulesRoot;

            results.Add(new ParsedLocalMod
            {
                Kind = LocalModKind.Dll,
                RelativePath = relativePath,
                Title = title,
                Version = version,
                IsEnabled = true,
                DirectoryPath = directoryPath,
                LocalIconPath = FindLocalIcon(directoryPath),
            });
        }

        foreach (var directory in Directory.EnumerateDirectories(currentPath))
            results.AddRange(FindStandaloneDlls(modulesRoot, directory, referencedDlls));

        return results;
    }

    public static string? FindLocalIcon(string directoryPath)
    {
        foreach (var fileName in new[] { "Icon.png", "icon.png", "Icon.jpg", "icon.jpg", "Icon.webp", "icon.webp" })
        {
            var iconPath = Path.Combine(directoryPath, fileName);
            if (File.Exists(iconPath))
                return iconPath;
        }

        return null;
    }
}
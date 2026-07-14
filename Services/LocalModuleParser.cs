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

        return ScanModulesRoot(InstalledModsService.GetModsFolder(gamePath));
    }

    /// <summary>Scan an arbitrary Modules root (profile store or game junction target).</summary>
    public IReadOnlyList<ParsedLocalMod> ScanModulesRoot(string modulesRoot)
    {
        if (string.IsNullOrWhiteSpace(modulesRoot) || !Directory.Exists(modulesRoot))
            return [];

        var referencedDlls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parsedModules = new List<ParsedLocalMod>();

        FindModuleFiles(modulesRoot, modulesRoot, referencedDlls, parsedModules);

        var parsedDlls = FindStandaloneDlls(modulesRoot, modulesRoot, referencedDlls);
        parsedModules.AddRange(parsedDlls);

        var disabledDlls = FindDisabledDlls(modulesRoot, modulesRoot, referencedDlls);
        parsedModules.AddRange(disabledDlls);

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
        try
        {
            var json = File.ReadAllText(moduleFilePath);
            var enabledLiteral = isEnabled ? "true" : "false";
            var updated = System.Text.RegularExpressions.Regex.Replace(
                json,
                """("IsEnabled"\s*:\s*)(true|false)""",
                $"$1{enabledLiteral}",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (!string.Equals(updated, json, StringComparison.Ordinal))
            {
                File.WriteAllText(moduleFilePath, updated);
                return true;
            }

            var config = TryReadConfig(moduleFilePath);
            if (config is null)
                return false;

            config.IsEnabled = isEnabled;
            var serialized = JsonSerializer.Serialize(config, new JsonSerializerOptions
            {
                WriteIndented = true,
            });
            File.WriteAllText(moduleFilePath, serialized);
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

            foreach (var resolvedPath in GetConfiguredAssemblyPaths(moduleFile))
                referencedDlls.Add(resolvedPath);

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

        var dependencyNames = config.Dependencies
            .Where(dep => !string.IsNullOrWhiteSpace(dep.Name))
            .Where(dep => !IgnoredDependencyNames.Contains(dep.Name))
            .Select(dep => dep.Name)
            .ToList();

        var dependencies = config.Dependencies
            .Where(dep => dependencyNames.Contains(dep.Name))
            .Select(dep => string.IsNullOrWhiteSpace(dep.Version)
                ? dep.Name
                : $"{dep.Name} ({dep.Version})")
            .ToList();

        var assemblyEntries = config.Assemblies
            .Where(assembly => !string.IsNullOrWhiteSpace(assembly.Path))
            .Select(assembly => new
            {
                Label = FormatAssemblyLabel(assembly),
                ResolvedPath = ResolveAssemblyPath(directoryPath, assembly.Path),
                DisabledPath = ResolveAssemblyPath(directoryPath, assembly.Path) + DllDisableHelper.DisabledSuffix,
            })
            .Where(entry => File.Exists(entry.ResolvedPath) || File.Exists(entry.DisabledPath))
            .ToList();

        return new ParsedLocalMod
        {
            Kind = LocalModKind.Module,
            RelativePath = relativePath,
            Title = title,
            ModuleName = title,
            Version = string.IsNullOrWhiteSpace(config.Version) ? "1.0.0.0" : config.Version,
            IsEnabled = config.IsEnabled,
            ModuleFilePath = moduleFilePath,
            DirectoryPath = directoryPath,
            LocalIconPath = FindLocalIcon(directoryPath),
            DependencyNames = dependencyNames,
            Dependencies = dependencies,
            Assemblies = assemblyEntries.Select(entry => entry.Label).ToList(),
            ResolvedAssemblyPaths = assemblyEntries.Select(entry => entry.ResolvedPath).ToList(),
        };
    }

    public static bool TrySetAssembliesEnabled(string moduleFilePath, bool enabled)
    {
        var config = TryReadConfig(moduleFilePath);
        if (config is null)
            return false;

        var directoryPath = Path.GetDirectoryName(moduleFilePath);
        if (string.IsNullOrWhiteSpace(directoryPath))
            return false;

        foreach (var assembly in config.Assemblies)
        {
            if (string.IsNullOrWhiteSpace(assembly.Path))
                continue;

            var activePath = ResolveAssemblyPath(directoryPath, assembly.Path);
            var disabledPath = activePath + DllDisableHelper.DisabledSuffix;
            if (!File.Exists(activePath) && !File.Exists(disabledPath))
                continue;

            if (!DllDisableHelper.TrySetEnabledAbsolute(activePath, enabled))
                return false;
        }

        return true;
    }

    public static IEnumerable<string> GetConfiguredAssemblyPaths(string moduleFilePath)
    {
        var config = TryReadConfig(moduleFilePath);
        if (config is null)
            yield break;

        var directoryPath = Path.GetDirectoryName(moduleFilePath);
        if (string.IsNullOrWhiteSpace(directoryPath))
            yield break;

        foreach (var assembly in config.Assemblies)
        {
            if (string.IsNullOrWhiteSpace(assembly.Path))
                continue;

            var activePath = ResolveAssemblyPath(directoryPath, assembly.Path);
            yield return activePath;
            yield return activePath + DllDisableHelper.DisabledSuffix;
        }
    }

    /// <summary>
    /// Match Unturned module loader: DirectoryPath + assembly.Path (not Path.Combine).
    /// </summary>
    public static string ResolveAssemblyPath(string moduleDirectory, string assemblyPath)
    {
        try
        {
            return Path.GetFullPath(moduleDirectory + assemblyPath);
        }
        catch
        {
            return moduleDirectory + assemblyPath;
        }
    }

    private static string FormatAssemblyLabel(UnturnedModuleAssembly assembly)
    {
        var fileName = Path.GetFileName(assembly.Path.TrimStart('/', '\\'));
        if (string.IsNullOrWhiteSpace(assembly.Role) ||
            string.Equals(assembly.Role, "None", StringComparison.OrdinalIgnoreCase))
            return fileName;

        return $"{fileName} ({assembly.Role})";
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

    private static List<ParsedLocalMod> FindDisabledDlls(
        string modulesRoot,
        string currentPath,
        HashSet<string> referencedDlls)
    {
        var results = new List<ParsedLocalMod>();

        foreach (var disabledPath in Directory.EnumerateFiles(currentPath, $"*.dll{DllDisableHelper.DisabledSuffix}"))
        {
            var activePath = disabledPath[..^DllDisableHelper.DisabledSuffix.Length];
            if (referencedDlls.Contains(Path.GetFullPath(activePath)))
                continue;

            var relativePath = Path.GetRelativePath(modulesRoot, activePath);
            AssemblyName? assemblyName = null;
            try
            {
                assemblyName = AssemblyName.GetAssemblyName(disabledPath);
            }
            catch
            {
                // Keep file-name based metadata when the DLL cannot be inspected.
            }

            var title = assemblyName?.Name ?? Path.GetFileNameWithoutExtension(activePath);
            var version = assemblyName?.Version?.ToString() ?? "—";
            var directoryPath = Path.GetDirectoryName(activePath) ?? modulesRoot;

            results.Add(new ParsedLocalMod
            {
                Kind = LocalModKind.Dll,
                RelativePath = relativePath,
                Title = title,
                Version = version,
                IsEnabled = false,
                DirectoryPath = directoryPath,
                LocalIconPath = FindLocalIcon(directoryPath),
            });
        }

        foreach (var directory in Directory.EnumerateDirectories(currentPath))
            results.AddRange(FindDisabledDlls(modulesRoot, directory, referencedDlls));

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
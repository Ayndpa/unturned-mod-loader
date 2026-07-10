using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace UnturnedModLoader.Services;

public record SteamDetectionResult(bool Success, string? GamePath, string Message);

public static class SteamLocator
{
    private const string UnturnedFolder = "Unturned";
    private const int UnturnedAppId = 304930;

    private static readonly string[] SteamRegistryPaths =
    [
        @"SOFTWARE\WOW6432Node\Valve\Steam",
        @"SOFTWARE\Valve\Steam",
    ];

    [SupportedOSPlatform("windows")]
    public static SteamDetectionResult DetectUnturned()
    {
        if (!OperatingSystem.IsWindows())
            return new(false, null, "自动检测仅支持 Windows 系统。");

        var libraries = GetSteamLibraryPaths();
        if (libraries.Count == 0)
            return new(false, null, "未在注册表中找到 Steam 安装路径，请手动选择游戏目录。");

        foreach (var library in libraries)
        {
            var fromManifest = TryResolveFromManifest(library);
            if (fromManifest is not null)
                return new(true, fromManifest, "已通过 Steam 库自动检测到 Unturned。");

            var defaultPath = Path.Combine(library, "steamapps", "common", UnturnedFolder);
            if (GamePathValidator.IsValid(defaultPath))
                return new(true, defaultPath, "已通过 Steam 库自动检测到 Unturned。");
        }

        return new(false, null,
            "已找到 Steam 安装，但未检测到 Unturned。请确认游戏已安装，或手动选择游戏目录。");
    }

    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<string> GetSteamLibraryPaths()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var steamPath = GetSteamInstallPath();
        if (!string.IsNullOrWhiteSpace(steamPath))
            paths.Add(NormalizePath(steamPath));

        if (!string.IsNullOrWhiteSpace(steamPath))
        {
            foreach (var vdf in GetLibraryFoldersVdfCandidates(steamPath))
            {
                if (!File.Exists(vdf))
                    continue;

                try
                {
                    var content = File.ReadAllText(vdf);
                    foreach (var lib in ParseLibraryPaths(content))
                        paths.Add(NormalizePath(lib));
                }
                catch
                {
                    // Skip unreadable VDF files.
                }
            }
        }

        return paths.ToList();
    }

    [SupportedOSPlatform("windows")]
    private static string? GetSteamInstallPath()
    {
        foreach (var subKey in SteamRegistryPaths)
        {
            var path = ReadRegistryString(Registry.LocalMachine, subKey, "InstallPath");
            if (!string.IsNullOrWhiteSpace(path))
                return path;
        }

        return ReadRegistryString(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath");
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadRegistryString(RegistryKey root, string subKey, string valueName)
    {
        try
        {
            using var key = root.OpenSubKey(subKey);
            return key?.GetValue(valueName) as string;
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> GetLibraryFoldersVdfCandidates(string steamPath)
    {
        yield return Path.Combine(steamPath, "config", "libraryfolders.vdf");
        yield return Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
    }

    internal static IEnumerable<string> ParseLibraryPaths(string vdfContent)
    {
        foreach (Match match in Regex.Matches(vdfContent, "\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase))
            yield return UnescapeVdfPath(match.Groups[1].Value);

        foreach (Match match in Regex.Matches(vdfContent, "\"\\d+\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase))
        {
            var value = UnescapeVdfPath(match.Groups[1].Value);
            if (Directory.Exists(value) || value.Contains(':'))
                yield return value;
        }
    }

    private static string? TryResolveFromManifest(string libraryRoot)
    {
        var manifestPath = Path.Combine(libraryRoot, "steamapps", $"appmanifest_{UnturnedAppId}.acf");
        if (!File.Exists(manifestPath))
            return null;

        try
        {
            var content = File.ReadAllText(manifestPath);
            var dirMatch = Regex.Match(content, "\"installdir\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase);
            if (!dirMatch.Success)
                return null;

            var installDir = UnescapeVdfPath(dirMatch.Groups[1].Value);
            var gamePath = Path.Combine(libraryRoot, "steamapps", "common", installDir);
            return GamePathValidator.IsValid(gamePath) ? gamePath : null;
        }
        catch
        {
            return null;
        }
    }

    private static string UnescapeVdfPath(string value) =>
        value.Replace(@"\\", @"\", StringComparison.Ordinal);

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
}
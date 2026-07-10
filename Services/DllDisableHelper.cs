namespace UnturnedModLoader.Services;

public static class DllDisableHelper
{
    public const string DisabledSuffix = ".disabled";

    public static string GetActivePath(string modsFolder, string relativeDllPath) =>
        Path.Combine(modsFolder, relativeDllPath.Replace('/', Path.DirectorySeparatorChar));

    public static string GetDisabledPath(string modsFolder, string relativeDllPath) =>
        GetActivePath(modsFolder, relativeDllPath) + DisabledSuffix;

    public static bool IsDisabledPath(string filePath) =>
        filePath.EndsWith(".dll" + DisabledSuffix, StringComparison.OrdinalIgnoreCase);

    public static string ToActiveRelativePath(string modulesRoot, string disabledFilePath)
    {
        var relativeDisabled = Path.GetRelativePath(modulesRoot, disabledFilePath);
        if (!relativeDisabled.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase))
            return relativeDisabled;

        return relativeDisabled[..^DisabledSuffix.Length];
    }

    public static bool TrySetEnabled(string modsFolder, string relativeDllPath, bool enabled) =>
        TrySetEnabledAbsolute(GetActivePath(modsFolder, relativeDllPath), enabled);

    public static bool TrySetEnabledAbsolute(string activePath, bool enabled)
    {
        var disabledPath = activePath + DisabledSuffix;

        try
        {
            if (enabled)
            {
                if (!File.Exists(disabledPath))
                    return File.Exists(activePath);

                Directory.CreateDirectory(Path.GetDirectoryName(activePath)!);
                if (File.Exists(activePath))
                    File.Delete(activePath);

                File.Move(disabledPath, activePath);
            }
            else
            {
                if (!File.Exists(activePath))
                    return File.Exists(disabledPath);

                Directory.CreateDirectory(Path.GetDirectoryName(disabledPath)!);
                if (File.Exists(disabledPath))
                    File.Delete(disabledPath);

                File.Move(activePath, disabledPath);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
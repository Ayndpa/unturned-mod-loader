using System.Diagnostics;

namespace UnturnedModLoader.Services;

public static class GameProcessService
{
    private const string ProcessName = "Unturned";

    /// <summary>
    /// Reports whether the game is running. When <paramref name="vfsDrive"/> is
    /// given, matches the process main module against <c>&lt;drive&gt;:\</c>; otherwise
    /// falls back to the real game path (when valid) or just the process name.
    /// </summary>
    public static bool IsRunning(string? gamePath = null, char? vfsDrive = null)
    {
        var processes = Process.GetProcessesByName(ProcessName);
        if (processes.Length == 0)
            return false;

        // When launched from the virtual drive, the canonical check is the drive prefix.
        if (vfsDrive is not null)
        {
            try
            {
                var prefix = $"{char.ToUpperInvariant(vfsDrive.Value)}:{Path.DirectorySeparatorChar}";
                foreach (var process in processes)
                {
                    try
                    {
                        var fileName = process.MainModule?.FileName;
                        if (fileName is not null &&
                            fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                    catch
                    {
                        // Access denied for elevated/foreign processes - stay safe.
                        return true;
                    }
                }
                return false;
            }
            finally
            {
                foreach (var process in processes)
                    process.Dispose();
            }
        }

        try
        {
            if (!GamePathValidator.IsValid(gamePath))
                return true;

            var expectedExe = Path.GetFullPath(Path.Combine(gamePath!, GamePathValidator.ExecutableName));
            foreach (var process in processes)
            {
                try
                {
                    var fileName = process.MainModule?.FileName;
                    if (fileName is not null &&
                        string.Equals(Path.GetFullPath(fileName), expectedExe, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch
                {
                    // Access denied for elevated or foreign processes — treat as running to stay safe.
                    return true;
                }
            }

            return false;
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }

    /// <summary>
    /// Launches the game from the WinFsp virtual drive (<c>&lt;drive&gt;:\Unturned.exe</c>).
    /// </summary>
    public static bool TryLaunchFromVirtualDrive(char driveLetter, out string? error)
    {
        error = null;
        var root = $"{char.ToUpperInvariant(driveLetter)}:{Path.DirectorySeparatorChar}";
        var exe = Path.Combine(root, GamePathValidator.ExecutableName);
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = root,
                UseShellExecute = true,
            });
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Diagnostic fallback: launches from a real (non-virtual) game path.</summary>
    public static bool TryLaunch(string gamePath, out string? error)
    {
        error = null;
        if (!GamePathValidator.IsValid(gamePath))
        {
            error = "Game path is not valid.";
            return false;
        }

        var exe = Path.Combine(gamePath, GamePathValidator.ExecutableName);
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = gamePath,
                UseShellExecute = true,
            });
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}

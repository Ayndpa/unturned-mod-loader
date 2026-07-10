using System.Diagnostics;

namespace UnturnedModLoader.Services;

public static class GameProcessService
{
    private const string ProcessName = "Unturned";

    public static bool IsRunning(string? gamePath = null)
    {
        var processes = Process.GetProcessesByName(ProcessName);
        if (processes.Length == 0)
            return false;

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
}
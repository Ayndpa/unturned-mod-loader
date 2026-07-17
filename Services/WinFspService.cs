using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;
using UnturnedModLoader.Services.WinFsp;

namespace UnturnedModLoader.Services;

public enum WinFspInstallState
{
    NotApplicable,
    Installed,
    NotInstalled,
}

public sealed record WinFspStatus(WinFspInstallState State, string? Version, string Detail);

/// <summary>
/// Detects WinFsp (Windows File System Proxy) and launches bundled elevated install script.
/// </summary>
public static class WinFspService
{
    public const string LauncherServiceName = "WinFsp.Launcher";

    public static string BundledDir =>
        Path.Combine(AppContext.BaseDirectory, "WinFsp");

    public static string InstallScriptPath =>
        Path.Combine(BundledDir, "Install-WinFsp.ps1");

    public static string CheckScriptPath =>
        Path.Combine(BundledDir, "Check-WinFsp.ps1");

    public static WinFspStatus GetStatus()
    {
        if (!OperatingSystem.IsWindows())
            return new WinFspStatus(WinFspInstallState.NotApplicable, null, "Windows only");

        if (TryDetectInstalled(out var version, out var detail))
            return new WinFspStatus(WinFspInstallState.Installed, version, detail);

        return new WinFspStatus(WinFspInstallState.NotInstalled, null, detail);
    }

    public static bool IsScriptBundlePresent() =>
        File.Exists(InstallScriptPath) && File.Exists(CheckScriptPath);

    /// <summary>
    /// Opens an elevated PowerShell window to run the bundled installer (UAC prompt).
    /// The chosen <paramref name="mirror"/> is forwarded to the script so the MSI is
    /// downloaded from the fastest source the user just measured.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static (bool Started, string Message) StartElevatedInstall(WinFspMirror mirror = WinFspMirror.Direct)
    {
        if (!File.Exists(InstallScriptPath))
            return (false, InstallScriptPath);

        var ps = FindPowerShellExe();
        if (ps is null)
            return (false, "PowerShell not found");

        // -NoExit keeps the elevated window open if the script errors before Read-Host.
        var arg = $"-NoProfile -ExecutionPolicy Bypass -NoExit -File \"{InstallScriptPath}\" -Mirror {mirror}";
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = ps,
                Arguments = arg,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = BundledDir,
            });
            return (true, "");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    [SupportedOSPlatform("windows")]
    public static WinFspStatus RefreshFromScript()
    {
        if (!File.Exists(CheckScriptPath))
            return GetStatus();

        try
        {
            var ps = FindPowerShellExe();
            if (ps is null)
                return GetStatus();

            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = ps,
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{CheckScriptPath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = BundledDir,
            });
            if (proc is null)
                return GetStatus();

            proc.WaitForExit(30_000);
            var stdout = proc.StandardOutput.ReadToEnd().Trim();
            if (proc.ExitCode == 0 && stdout.StartsWith("INSTALLED", StringComparison.OrdinalIgnoreCase))
            {
                var version = stdout.Length > 10 ? stdout["INSTALLED:".Length..].Trim() : null;
                return new WinFspStatus(WinFspInstallState.Installed, version, stdout);
            }

            if (!string.IsNullOrWhiteSpace(stdout))
                return new WinFspStatus(WinFspInstallState.NotInstalled, null, stdout);

            var err = proc.StandardError.ReadToEnd().Trim();
            return new WinFspStatus(WinFspInstallState.NotInstalled, null,
                string.IsNullOrWhiteSpace(err) ? "NOT_INSTALLED" : err);
        }
        catch (Exception ex)
        {
            return new WinFspStatus(WinFspInstallState.NotInstalled, null, ex.Message);
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool TryDetectInstalled(out string? version, out string detail)
    {
        version = null;
        detail = "";

        if (IsLauncherServicePresent())
        {
            detail = $"Service {LauncherServiceName}";
            version = ReadRegistryVersion();
            return true;
        }

        foreach (var dllPath in GetCandidateDllPaths())
        {
            if (File.Exists(dllPath))
            {
                detail = dllPath;
                version = ReadRegistryVersion();
                return true;
            }
        }

        if (ReadRegistryVersion() is { } regVer)
        {
            version = regVer;
            detail = "Registry";
            return true;
        }

        detail = "NOT_INSTALLED";
        return false;
    }

    [SupportedOSPlatform("windows")]
    private static bool IsLauncherServicePresent()
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = $"query {LauncherServiceName}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });
            if (proc is null)
                return false;

            proc.WaitForExit(5000);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadRegistryVersion()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                foreach (var sub in new[] { @"SOFTWARE\WinFsp", @"SOFTWARE\WinFsp.Launcher" })
                {
                    using var key = baseKey.OpenSubKey(sub);
                    var ver = key?.GetValue("Version") as string
                              ?? key?.GetValue("InstalledVersion") as string;
                    if (!string.IsNullOrWhiteSpace(ver))
                        return ver.Trim();
                }
            }
            catch
            {
                // ignore
            }
        }

        return null;
    }

    private static IEnumerable<string> GetCandidateDllPaths()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        };

        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;
            yield return Path.Combine(root, "WinFsp", "bin", "winfsp-x64.dll");
            yield return Path.Combine(root, "WinFsp", "bin", "winfsp-x86.dll");
        }
    }

    private static string? FindPowerShellExe()
    {
        var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var ps5 = Path.Combine(windir, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        if (File.Exists(ps5))
            return ps5;

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pwsh7 = Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe");
        return File.Exists(pwsh7) ? pwsh7 : null;
    }
}
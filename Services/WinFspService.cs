using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using UnturnedModLoader.Services.WinFsp;

namespace UnturnedModLoader.Services;

public enum WinFspInstallState
{
    NotApplicable,
    /// <summary>Correct native version matching <see cref="WinFspMirrorService.RequiredNativeVersion"/>.</summary>
    Installed,
    /// <summary>No WinFsp detected.</summary>
    NotInstalled,
    /// <summary>Some WinFsp is present but not the version this app's managed binding expects.</summary>
    WrongVersion,
}

public sealed record WinFspStatus(
    WinFspInstallState State,
    string? Version,
    string Detail);

public enum WinFspInstallPhase
{
    Resolving,
    Downloading,
    Uninstalling,
    Installing,
    Verifying,
    Remounting,
    Done,
    Failed,
}

public sealed record WinFspInstallProgress(
    WinFspInstallPhase Phase,
    string Message,
    double? Percent);

public sealed record WinFspInstallResult(
    bool Success,
    string Message,
    WinFspStatus Status);

/// <summary>
/// Detects WinFsp and installs the native MSI that matches the managed <c>winfsp.net</c>
/// package — entirely in-process (multi-part download + elevated msiexec), no PowerShell.
/// </summary>
public static class WinFspService
{
    public const string LauncherServiceName = "WinFsp.Launcher";

    private static readonly Regex ProductCodeRegex = new(
        @"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string CacheDir =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UnturnedModLoader",
            "cache");

    public static WinFspStatus GetStatus()
    {
        if (!OperatingSystem.IsWindows())
            return new WinFspStatus(WinFspInstallState.NotApplicable, null, "Windows only");

        if (!TryDetectInstalled(out var version, out var detail))
            return new WinFspStatus(WinFspInstallState.NotInstalled, null, detail);

        if (IsRequiredVersion(version))
            return new WinFspStatus(WinFspInstallState.Installed, version, detail);

        return new WinFspStatus(
            WinFspInstallState.WrongVersion,
            version,
            string.IsNullOrWhiteSpace(version)
                ? $"Installed, need {WinFspMirrorService.RequiredNativeVersion}"
                : $"Installed {version}, need {WinFspMirrorService.RequiredNativeVersion}");
    }

    /// <summary>True when install / upgrade should be offered.</summary>
    public static bool NeedsInstall(WinFspStatus status) =>
        status.State is WinFspInstallState.NotInstalled or WinFspInstallState.WrongVersion;

    public static bool IsRequiredVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return false;

        // Accept "2.2.26194", "2.2.26194.0", registry strings with prefixes.
        var required = WinFspMirrorService.RequiredNativeVersion;
        if (version.Equals(required, StringComparison.OrdinalIgnoreCase))
            return true;
        if (version.StartsWith(required + ".", StringComparison.OrdinalIgnoreCase))
            return true;

        // Compare major.minor.build when both parse.
        if (TryParseLooseVersion(version, out var have) && TryParseLooseVersion(required, out var need))
            return have.Major == need.Major && have.Minor == need.Minor && have.Build == need.Build;

        return false;
    }

    /// <summary>
    /// Download the matching MSI (multi-part), uninstall any other WinFsp product, then
    /// elevated-install. Reports progress; never throws for expected failures (UAC cancel, etc.).
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static async Task<WinFspInstallResult> InstallOrUpgradeAsync(
        WinFspMirror mirror = WinFspMirror.Direct,
        IProgress<WinFspInstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Fail(WinFspInstallPhase.Failed, "Windows only", progress);
        }

        try
        {
            Report(progress, WinFspInstallPhase.Resolving, "Resolving WinFsp package…");

            var rawUrl = await WinFspMirrorService.ResolveMsiAssetUrlAsync(ct);
            var mirrored = WinFspMirrorService.ApplyMirror(rawUrl, mirror);
            var fileName = ExtractFileName(rawUrl) ?? $"winfsp-{WinFspMirrorService.RequiredNativeVersion}.msi";
            Directory.CreateDirectory(CacheDir);
            var msiPath = Path.Combine(CacheDir, fileName);

            var needDownload = !File.Exists(msiPath) || new FileInfo(msiPath).Length < 1024;
            if (needDownload)
            {
                Report(progress, WinFspInstallPhase.Downloading, $"Downloading {fileName}…", 0);
                var tmp = msiPath + ".download";
                try
                {
                    if (File.Exists(tmp))
                        File.Delete(tmp);

                    var dlProgress = new Progress<MultiPartDownloader.Progress>(p =>
                    {
                        var pct = p.Percent;
                        var msg = pct is { } v
                            ? $"Downloading {fileName}… {v:0.0}%"
                            : $"Downloading {fileName}… {FormatBytes(p.BytesReceived)}";
                        Report(progress, WinFspInstallPhase.Downloading, msg, pct);
                    });

                    await MultiPartDownloader.DownloadAsync(mirrored, tmp, dlProgress, parts: 8, ct);
                    if (File.Exists(msiPath))
                        File.Delete(msiPath);
                    File.Move(tmp, msiPath);
                }
                catch
                {
                    try { if (File.Exists(tmp)) File.Delete(tmp); }
                    catch { /* ignore */ }
                    throw;
                }
            }

            var current = GetStatus();
            if (current.State == WinFspInstallState.Installed)
            {
                Report(progress, WinFspInstallPhase.Done, "Already up to date.");
                return new WinFspInstallResult(true, "Already installed", current);
            }

            // One elevated cmd: optional uninstall of every WinFsp product, then install.
            // Keeps a single UAC prompt for upgrade paths.
            var productCodes = FindWinFspProductCodes().ToList();
            if (productCodes.Count > 0)
                Report(progress, WinFspInstallPhase.Uninstalling, "Uninstalling previous WinFsp…");
            else
                Report(progress, WinFspInstallPhase.Installing, $"Installing {fileName}…");

            var cmd = BuildInstallCommandLine(productCodes, msiPath);
            var install = await RunElevatedCommandAsync(cmd, ct);
            if (install.Cancelled)
                return Fail(WinFspInstallPhase.Failed, "UAC cancelled", progress, uac: true);
            if (!install.Success)
            {
                return Fail(
                    WinFspInstallPhase.Failed,
                    $"install exit {install.ExitCode}: {install.Message}",
                    progress);
            }

            // Fresh service / DLL may need a brief moment to appear.
            Report(progress, WinFspInstallPhase.Verifying, "Verifying installation…");
            WinFspStatus status = GetStatus();
            for (var i = 0; i < 10 && status.State != WinFspInstallState.Installed; i++)
            {
                await Task.Delay(500, ct);
                status = GetStatus();
            }

            if (status.State != WinFspInstallState.Installed)
            {
                return Fail(
                    WinFspInstallPhase.Failed,
                    status.State == WinFspInstallState.WrongVersion
                        ? $"Installed version mismatch ({status.Version ?? "?"})."
                        : "Install finished but WinFsp was not detected.",
                    progress);
            }

            Report(progress, WinFspInstallPhase.Done, "WinFsp installed.", 100);
            return new WinFspInstallResult(true, "Installed", status);
        }
        catch (OperationCanceledException)
        {
            return Fail(WinFspInstallPhase.Failed, "Cancelled", progress);
        }
        catch (Exception ex)
        {
            return Fail(WinFspInstallPhase.Failed, ex.GetBaseException().Message, progress);
        }
    }

    private static WinFspInstallResult Fail(
        WinFspInstallPhase phase,
        string message,
        IProgress<WinFspInstallProgress>? progress,
        bool uac = false)
    {
        Report(progress, phase, message);
        var status = OperatingSystem.IsWindows()
            ? GetStatus()
            : new WinFspStatus(WinFspInstallState.NotApplicable, null, "Windows only");
        return new WinFspInstallResult(false, uac ? "UAC_CANCELLED" : message, status);
    }

    private static void Report(
        IProgress<WinFspInstallProgress>? progress,
        WinFspInstallPhase phase,
        string message,
        double? percent = null) =>
        progress?.Report(new WinFspInstallProgress(phase, message, percent));

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.0} KB";
        return $"{bytes / (1024.0 * 1024.0):0.0} MB";
    }

    private static string? ExtractFileName(string url)
    {
        try
        {
            var path = new Uri(url).AbsolutePath;
            var name = Path.GetFileName(path);
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch
        {
            return null;
        }
    }

    private sealed record MsiexecResult(bool Success, bool Cancelled, bool NothingToDo, int ExitCode, string Message);

    /// <summary>
    /// Build a single <c>cmd /c</c> line: uninstall each product code, then install the MSI.
    /// Uses <c>&amp;</c> so a missing product (1605) does not abort the install step.
    /// </summary>
    private static string BuildInstallCommandLine(IReadOnlyList<string> productCodes, string msiPath)
    {
        var parts = new List<string>(productCodes.Count + 1);
        foreach (var code in productCodes)
            parts.Add($"msiexec.exe /x {code} /qn /norestart");
        parts.Add($"msiexec.exe /i \"{msiPath}\" /qn /norestart");
        // "call" is unnecessary; chain with & so each step runs. Exit code will be the last msiexec.
        return "/c " + string.Join(" & ", parts);
    }

    [SupportedOSPlatform("windows")]
    private static async Task<MsiexecResult> RunElevatedCommandAsync(string cmdArguments, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = cmdArguments,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            using var proc = Process.Start(psi);
            if (proc is null)
                return new MsiexecResult(false, false, false, -1, "Failed to start elevated installer");

            await proc.WaitForExitAsync(ct);
            var code = proc.ExitCode;
            // 0 = success, 3010 = success reboot required
            if (code is 0 or 3010)
                return new MsiexecResult(true, false, false, code, "OK");

            return new MsiexecResult(false, false, false, code, $"exit {code}");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new MsiexecResult(false, true, false, 1223, "UAC cancelled");
        }
        catch (Exception ex)
        {
            return new MsiexecResult(false, false, false, -1, ex.Message);
        }
    }

    /// <summary>Locate MSI product codes for anything that looks like WinFsp.</summary>
    [SupportedOSPlatform("windows")]
    public static IEnumerable<string> FindWinFspProductCodes()
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            foreach (var root in new[]
                     {
                         @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                         @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
                     })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                    using var uninstall = baseKey.OpenSubKey(root);
                    if (uninstall is null)
                        continue;

                    foreach (var subName in uninstall.GetSubKeyNames())
                    {
                        using var key = uninstall.OpenSubKey(subName);
                        if (key is null)
                            continue;

                        var display = key.GetValue("DisplayName") as string
                                      ?? key.GetValue("QuietDisplayName") as string
                                      ?? "";
                        var publisher = key.GetValue("Publisher") as string ?? "";

                        var looksLikeWinFsp =
                            display.Contains("WinFsp", StringComparison.OrdinalIgnoreCase)
                            || display.Contains("Windows File System Proxy", StringComparison.OrdinalIgnoreCase)
                            || (publisher.Contains("Bill Zissimopoulos", StringComparison.OrdinalIgnoreCase)
                                && display.Contains("Fsp", StringComparison.OrdinalIgnoreCase));

                        if (!looksLikeWinFsp)
                            continue;

                        var code = subName;
                        if (!ProductCodeRegex.IsMatch(code))
                        {
                            var fromValue = key.GetValue("UninstallString") as string ?? "";
                            var m = ProductCodeRegex.Match(fromValue);
                            if (m.Success)
                                code = m.Value;
                            else
                                continue;
                        }

                        found.Add(code);
                    }
                }
                catch
                {
                    // ignore hive access issues
                }
            }
        }

        return found;
    }

    [SupportedOSPlatform("windows")]
    private static bool TryDetectInstalled(out string? version, out string detail)
    {
        version = null;
        detail = "";

        if (IsLauncherServicePresent())
        {
            detail = $"Service {LauncherServiceName}";
            version = ReadInstalledVersion();
            return true;
        }

        foreach (var dllPath in GetCandidateDllPaths())
        {
            if (File.Exists(dllPath))
            {
                detail = dllPath;
                version = ReadFileVersion(dllPath) ?? ReadInstalledVersion();
                return true;
            }
        }

        if (ReadInstalledVersion() is { } regVer)
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
    private static string? ReadInstalledVersion()
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

        // Prefer native DLL file version when registry is sparse.
        foreach (var dll in GetCandidateDllPaths())
        {
            var fv = ReadFileVersion(dll);
            if (fv is not null)
                return fv;
        }

        return null;
    }

    private static string? ReadFileVersion(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            var info = FileVersionInfo.GetVersionInfo(path);
            if (!string.IsNullOrWhiteSpace(info.FileVersion))
                return info.FileVersion.Trim();
            if (info.FileMajorPart != 0 || info.FileMinorPart != 0)
                return $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}";
        }
        catch
        {
            // ignore
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
            yield return Path.Combine(root, "WinFsp", "bin", "winfsp-a64.dll");
        }
    }

    private static bool TryParseLooseVersion(string text, out Version version)
    {
        version = new Version(0, 0);
        var m = Regex.Match(text, @"\d+\.\d+(\.\d+)?(\.\d+)?");
        if (!m.Success)
            return false;
        if (!Version.TryParse(m.Value, out var parsed))
            return false;
        version = parsed;
        return true;
    }
}

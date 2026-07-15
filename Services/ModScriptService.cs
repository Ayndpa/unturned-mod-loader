using System.Diagnostics;
using System.IO.Compression;
using UnturnedModLoader.Models;

namespace UnturnedModLoader.Services;

/// <summary>
/// Context handed to a developer install/uninstall script via environment variables.
/// Scripts must stage files under <see cref="OverlayDir"/> (mirrored into the game via the profile
/// overlay + junctions). They must NOT write into <see cref="GamePath"/> directly.
/// </summary>
public sealed record InstallContext
{
    /// <summary>install | uninstall</summary>
    public required string Action { get; init; }

    public required int ModId { get; init; }
    public required string ModTitle { get; init; }
    public required string ModVersion { get; init; }

    /// <summary>Unturned install root (read-only reference). Scripts must not modify it.</summary>
    public required string GamePath { get; init; }

    /// <summary>Profile overlay root. Stage mod files here (relative to the game root).</summary>
    public required string OverlayDir { get; init; }

    /// <summary>Extracted archive contents (incl. scripts). Scripts copy from here into the overlay.</summary>
    public required string StagingDir { get; init; }

    public bool GameRunning { get; init; }

    /// <summary>Convenience: overlay\Modules.</summary>
    public string ModulesDir => Path.Combine(OverlayDir, "Modules");

    public IReadOnlyDictionary<string, string?> AsEnvironmentVariables()
    {
        var dict = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["UNMOD_ACTION"] = Action,
            ["UNMOD_MOD_ID"] = ModId.ToString(),
            ["UNMOD_MOD_TITLE"] = ModTitle,
            ["UNMOD_MOD_VERSION"] = ModVersion,
            ["UNMOD_GAME_PATH"] = GamePath,
            ["UNMOD_OVERLAY_DIR"] = OverlayDir,
            ["UNMOD_STAGING_DIR"] = StagingDir,
            ["UNMOD_MODULES_DIR"] = ModulesDir,
            ["UNMOD_GAME_RUNNING"] = GameRunning ? "1" : "0",
        };
        return dict;
    }
}

/// <summary>
/// Runs developer-supplied install/uninstall scripts and records the overlay file diff so
/// uninstall can fall back to a manifest-driven cleanup if a script is incomplete.
/// </summary>
public sealed class ModScriptService
{
    /// <summary>Folder inside an archive that holds the scripts.</summary>
    public const string ScriptsFolder = "scripts";

    private static readonly string[] WindowsInstall = ["install.ps1"];
    private static readonly string[] WindowsUninstall = ["uninstall.ps1"];
    private static readonly string[] UnixInstall = ["install.sh"];
    private static readonly string[] UnixUninstall = ["uninstall.sh"];

    /// <summary>Locate the script for the current platform + action inside an extracted directory.</summary>
    public static string? FindScript(string stagingDir, string action)
    {
        var names = OperatingSystem.IsWindows()
            ? (action == InstallAction.Uninstall ? WindowsUninstall : WindowsInstall)
            : (action == InstallAction.Uninstall ? UnixUninstall : UnixInstall);

        var scriptsRoot = Path.Combine(stagingDir, ScriptsFolder);
        if (Directory.Exists(scriptsRoot))
        {
            foreach (var name in names)
            {
                var candidate = Path.Combine(scriptsRoot, name);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        // Tolerate flat layout (script at archive root).
        foreach (var name in names)
        {
            var candidate = Path.Combine(stagingDir, name);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    /// <summary>True if the archive (zip) or single file payload carries a script for this action.</summary>
    public static bool ArchiveHasScript(byte[] content, string fileName, string action)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (extension != ".zip")
            return false;

        var names = OperatingSystem.IsWindows()
            ? (action == InstallAction.Uninstall ? WindowsUninstall : WindowsInstall)
            : (action == InstallAction.Uninstall ? UnixUninstall : UnixInstall);

        try
        {
            using var stream = new MemoryStream(content);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            foreach (var entry in archive.Entries)
            {
                var rel = entry.FullName.Replace('\\', '/').TrimStart('/');
                var inScripts = rel.StartsWith(ScriptsFolder + "/", StringComparison.OrdinalIgnoreCase);
                var isFlat = !rel.Contains('/');
                if (!inScripts && !isFlat)
                    continue;

                foreach (var name in names)
                {
                    if (string.Equals(Path.GetFileName(rel), name, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
        }
        catch
        {
            // Malformed archive - treat as no script; fall back to default install.
        }

        return false;
    }

    /// <summary>
    /// Snapshot overlay file paths (relative to overlay root), keyed by normalized relative path.
    /// Captured before/after running a script to derive <see cref="InstalledMod.InstalledFiles"/>.
    /// </summary>
    public static Dictionary<string, string> SnapshotOverlay(string overlayDir)
    {
        var snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(overlayDir))
            return snapshot;

        foreach (var file in EnumerateFilesSafe(overlayDir))
        {
            var rel = Path.GetRelativePath(overlayDir, file).Replace('\\', '/');
            snapshot[rel] = file;
        }

        return snapshot;
    }

    /// <summary>Run a script with the install context injected as environment variables.</summary>
    public ScriptRunResult Run(string scriptPath, InstallContext context, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(scriptPath))
            return ScriptRunResult.Failed($"Script not found: {scriptPath}");

        var psi = OperatingSystem.IsWindows()
            ? BuildWindowsStartInfo(scriptPath)
            : BuildUnixStartInfo(scriptPath);

        psi.WorkingDirectory = context.StagingDir;

        foreach (var (key, value) in context.AsEnvironmentVariables())
            psi.Environment[key] = value;

        try
        {
            var process = new Process { StartInfo = psi };
            process.Start();

            // Drain output so the child cannot block on a full pipe.
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            // 5-minute cap - install scripts should be quick file copies.
            if (!process.WaitForExit(TimeSpan.FromMinutes(5)))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return ScriptRunResult.Failed("Script timed out after 5 minutes.");
            }

            var stdout = stdoutTask.IsCompleted ? stdoutTask.Result : "";
            var stderr = stderrTask.IsCompleted ? stderrTask.Result : "";

            return process.ExitCode == 0
                ? ScriptRunResult.Ok(stdout, stderr)
                : ScriptRunResult.Failed($"Script exited with code {process.ExitCode}.{(string.IsNullOrWhiteSpace(stderr) ? "" : "\n" + stderr)}");
        }
        catch (Exception ex)
        {
            return ScriptRunResult.Failed($"Failed to run script: {ex.Message}");
        }
    }

    private static ProcessStartInfo BuildWindowsStartInfo(string scriptPath)
    {
        // -ExecutionPolicy Bypass so the script runs even under restricted machine policy.
        // -NoProfile so a user's PowerShell profile cannot inject code into mod installs.
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(scriptPath);
        return psi;
    }

    private static ProcessStartInfo BuildUnixStartInfo(string scriptPath)
    {
        // Run under sh which honors the script's shebang (#!/bin/bash, #!/bin/zsh, …).
        // Avoids UseShellExecute so output stays redirectable.
        var psi = new ProcessStartInfo
        {
            FileName = "sh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(scriptPath);
        return psi;
    }

    private static IEnumerable<string> EnumerateFilesSafe(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            string[] files;
            string[] dirs;
            try
            {
                files = Directory.GetFiles(current);
                dirs = Directory.GetDirectories(current);
            }
            catch
            {
                continue;
            }

            foreach (var f in files)
                yield return f;
            foreach (var d in dirs)
                stack.Push(d);
        }
    }
}

public sealed record ScriptRunResult(bool Success, string? Error, string Stdout, string Stderr)
{
    public static ScriptRunResult Ok(string stdout, string stderr) =>
        new(true, null, stdout ?? "", stderr ?? "");

    public static ScriptRunResult Failed(string error) =>
        new(false, error, "", "");
}

public static class InstallAction
{
    public const string Install = "install";
    public const string Uninstall = "uninstall";
}

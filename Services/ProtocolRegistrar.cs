using System.Diagnostics;
using Microsoft.Win32;

namespace UnturnedModLoader.Services;

/// <summary>
/// Registers the <c>unmod://</c> custom URI scheme for the current user so browsers can launch
/// the loader with an install intent. Per-user (HKCU) - no elevation required.
/// </summary>
public static class ProtocolRegistrar
{
    public const string Scheme = "unmod";
    public const string ProtocolName = "Unturned Mod Loader";

    /// <summary>Register the scheme pointing at the current executable, if missing or stale.</summary>
    public static void EnsureRegistered()
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                return;

            using var key = Registry.CurrentUser.CreateSubKey($"SOFTWARE\\Classes\\{Scheme}");
            key.SetValue(null, $"URL:{ProtocolName}");
            key.SetValue("URL Protocol", "");

            using var cmdKey = key.CreateSubKey("shell\\open\\command");
            // "%1" passes the full unmod://install/123 URI as the first arg.
            var desired = $"\"{exePath}\" \"%1\"";
            var existing = cmdKey.GetValue(null) as string;
            if (!string.Equals(existing, desired, StringComparison.OrdinalIgnoreCase))
                cmdKey.SetValue(null, desired);
        }
        catch
        {
            // Registration is best-effort; web install falls back to a manual download prompt.
        }
    }

    /// <summary>Parse an <c>unmod://install/{id}</c> URI (or a raw id / --install arg) into a mod id.</summary>
    public static bool TryParseInstallIntent(string? arg, out int modId)
    {
        modId = 0;
        if (string.IsNullOrWhiteSpace(arg))
            return false;

        var raw = arg.Trim();

        // Bare integer: --install 123
        if (int.TryParse(raw, out var id))
        {
            modId = id;
            return true;
        }

        // unmod://install/123 or unmod:install/123
        if (raw.StartsWith($"{Scheme}://", StringComparison.OrdinalIgnoreCase))
            raw = raw[$"{Scheme}://".Length..];
        else if (raw.StartsWith($"{Scheme}:", StringComparison.OrdinalIgnoreCase))
            raw = raw[($"{Scheme}:").Length..];

        raw = raw.TrimStart('/');
        var slash = raw.IndexOf('/');
        var action = slash < 0 ? raw : raw[..slash];
        var rest = slash < 0 ? "" : raw[(slash + 1)..];

        if (!string.Equals(action, "install", StringComparison.OrdinalIgnoreCase))
            return false;

        // rest may be "123" or "123?token=..."
        var query = rest.IndexOf('?');
        if (query >= 0)
            rest = rest[..query];

        return int.TryParse(rest.TrimEnd('/'), out modId) && modId > 0;
    }
}

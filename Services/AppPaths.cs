namespace UnturnedModLoader.Services;

/// <summary>Shared AppData layout for UnturnedModLoader.</summary>
public static class AppPaths
{
    public static string AppDataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "UnturnedModLoader");

    public static string SettingsPath => Path.Combine(AppDataDir, "settings.json");
    public static string LegacyInstalledModsPath => Path.Combine(AppDataDir, "installed-mods.json");
    public static string LegacyQuarantineDir => Path.Combine(AppDataDir, "quarantine");
    public static string ProfilesRoot => Path.Combine(AppDataDir, "profiles");

    public static string ProfileDir(string profileId) =>
        Path.Combine(ProfilesRoot, SanitizeId(profileId));

    public static string ProfileMetaPath(string profileId) =>
        Path.Combine(ProfileDir(profileId), "profile.json");

    public static string ProfileInstalledModsPath(string profileId) =>
        Path.Combine(ProfileDir(profileId), "installed-mods.json");

    public static string ProfileQuarantineDir(string profileId) =>
        Path.Combine(ProfileDir(profileId), "quarantine");

    /// <summary>Profile content mirrored relative to the game install root (VFS upper layer).</summary>
    public static string ProfileOverlayDir(string profileId) =>
        Path.Combine(ProfileDir(profileId), "overlay");

    public static string ProfileModulesFolder(string profileId) =>
        Path.Combine(ProfileOverlayDir(profileId), "Modules");

    public static string GameModulesFolder(string gamePath) =>
        Path.Combine(gamePath, "Modules");

    public static void EnsureAppData() => Directory.CreateDirectory(AppDataDir);

    public static void EnsureProfileLayout(string profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId))
            throw new ArgumentException("Profile id is required.", nameof(profileId));

        Directory.CreateDirectory(ProfileDir(profileId));
        Directory.CreateDirectory(ProfileOverlayDir(profileId));
        Directory.CreateDirectory(ProfileModulesFolder(profileId));
    }

    private static string SanitizeId(string profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId))
            throw new ArgumentException("Profile id is required.", nameof(profileId));

        foreach (var c in Path.GetInvalidFileNameChars())
        {
            if (profileId.Contains(c))
                throw new ArgumentException($"Invalid profile id: {profileId}", nameof(profileId));
        }

        return profileId;
    }
}

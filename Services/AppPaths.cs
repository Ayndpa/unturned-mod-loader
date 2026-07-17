namespace UnturnedModLoader.Services;

/// <summary>Shared AppData layout for UnturnedModLoader.</summary>
public static class AppPaths
{
    public static string AppDataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "UnturnedModLoader");

    public static string SettingsPath => Path.Combine(AppDataDir, "settings.json");
    public static string ProfilesRoot => Path.Combine(AppDataDir, "profiles");

    public static string ProfileDir(string profileId) =>
        Path.Combine(ProfilesRoot, SanitizeId(profileId));

    public static string ProfileMetaPath(string profileId) =>
        Path.Combine(ProfileDir(profileId), "profile.json");

    /// <summary>Profile content mirrored relative to the game install root (VFS upper layer).</summary>
    public static string ProfileOverlayDir(string profileId) =>
        Path.Combine(ProfileDir(profileId), "overlay");

    /// <summary>
    /// Per-mod manifest files live here, one JSON per installed RemoteId. Sits inside the
    /// overlay so it travels with the profile; Unturned ignores the unknown directory.
    /// </summary>
    public static string ProfileManifestsDir(string profileId) =>
        Path.Combine(ProfileOverlayDir(profileId), ".unmod-manifests");

    public static void EnsureAppData() => Directory.CreateDirectory(AppDataDir);

    public static void EnsureProfileLayout(string profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId))
            throw new ArgumentException("Profile id is required.", nameof(profileId));

        Directory.CreateDirectory(ProfileDir(profileId));
        Directory.CreateDirectory(ProfileOverlayDir(profileId));
        Directory.CreateDirectory(ProfileManifestsDir(profileId));
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

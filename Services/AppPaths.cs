namespace UnturnedModLoader.Services;

/// <summary>Shared AppData layout for UnturnedModLoader.</summary>
public static class AppPaths
{
    public static string AppDataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "UnturnedModLoader");

    public static string SettingsPath => Path.Combine(AppDataDir, "settings.json");
    public static string MountStatePath => Path.Combine(AppDataDir, "mount-state.json");
    public static string ActiveOverlayStatePath => Path.Combine(AppDataDir, "active-overlay.json");
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

    /// <summary>Profile content mirrored relative to the game install root.</summary>
    public static string ProfileOverlayDir(string profileId) =>
        Path.Combine(ProfileDir(profileId), "overlay");

    /// <summary>Backups of vanilla files replaced while the profile was applied.</summary>
    public static string ProfileOriginalsDir(string profileId) =>
        Path.Combine(ProfileDir(profileId), "originals");

    public static string ProfileJournalPath(string profileId) =>
        Path.Combine(ProfileDir(profileId), "journal.json");

    /// <summary>Temporary pre-run file copies used to restore vanilla after runtime overwrites.</summary>
    public static string ProfileSessionBaselineDir(string profileId) =>
        Path.Combine(ProfileDir(profileId), "session-baseline");

    /// <summary>Legacy V1 modules store (migrated into overlay\Modules).</summary>
    public static string LegacyProfileMountsModules(string profileId) =>
        Path.Combine(ProfileDir(profileId), "mounts", "Modules");

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
        Directory.CreateDirectory(ProfileOriginalsDir(profileId));
        Directory.CreateDirectory(ProfileModulesFolder(profileId));

        // Migrate legacy mounts\Modules → overlay\Modules once.
        var legacy = LegacyProfileMountsModules(profileId);
        var modules = ProfileModulesFolder(profileId);
        if (Directory.Exists(legacy) && Directory.Exists(modules))
        {
            if (!Directory.EnumerateFileSystemEntries(modules).Any() &&
                Directory.EnumerateFileSystemEntries(legacy).Any())
            {
                try
                {
                    MoveDirectoryContents(legacy, modules);
                }
                catch
                {
                    // best effort
                }
            }
        }
    }

    private static void MoveDirectoryContents(string sourceDir, string destDir)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(sourceDir))
        {
            var name = Path.GetFileName(entry);
            var dest = Path.Combine(destDir, name);
            if (Directory.Exists(entry))
            {
                if (Directory.Exists(dest))
                    MoveDirectoryContents(entry, dest);
                else
                    Directory.Move(entry, dest);
            }
            else
            {
                if (File.Exists(dest))
                    File.Delete(dest);
                File.Move(entry, dest);
            }
        }
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

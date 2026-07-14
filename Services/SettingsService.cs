using System.Text.Json;
using UnturnedModLoader.Models;

namespace UnturnedModLoader.Services;

public class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _settingsPath;

    public SettingsService()
    {
        AppPaths.EnsureAppData();
        _settingsPath = AppPaths.SettingsPath;
    }

    public AppSettings Load()
    {
        if (!File.Exists(_settingsPath))
            return new AppSettings();

        try
        {
            var json = File.ReadAllText(_settingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        AppPaths.EnsureAppData();
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(_settingsPath, json);
    }

    /// <summary>
    /// Migrates v1 single-Modules layout into multi-profile overlay storage.
    /// </summary>
    public string? MigrateIfNeeded(AppSettings settings, GameOverlayService overlayService)
    {
        AppPaths.EnsureAppData();
        Directory.CreateDirectory(AppPaths.ProfilesRoot);

        var needsVersionBump = settings.SettingsVersion < 2;
        var legacyManifestExists = File.Exists(AppPaths.LegacyInstalledModsPath);
        var gameModules = GamePathValidator.IsValid(settings.GamePath)
            ? AppPaths.GameModulesFolder(settings.GamePath)
            : null;

        var realModulesHasContent = gameModules is not null
            && Directory.Exists(gameModules)
            && !JunctionHelper.IsJunction(gameModules)
            && Directory.EnumerateFileSystemEntries(gameModules).Any();

        // Also migrate any profile still using mounts\Modules only.
        if (Directory.Exists(AppPaths.ProfilesRoot))
        {
            foreach (var dir in Directory.EnumerateDirectories(AppPaths.ProfilesRoot))
            {
                var id = Path.GetFileName(dir);
                AppPaths.EnsureProfileLayout(id);
            }
        }

        if (!needsVersionBump && !legacyManifestExists && !realModulesHasContent)
        {
            if (string.IsNullOrWhiteSpace(settings.ActiveProfileId))
                settings.ActiveProfileId = GameProfile.VanillaId;
            if (settings.SettingsVersion < 2)
            {
                settings.SettingsVersion = 2;
                Save(settings);
            }
            return null;
        }

        if (settings.SettingsVersion < 2)
            settings.SettingsVersion = 2;

        if (string.IsNullOrWhiteSpace(settings.ActiveProfileId))
            settings.ActiveProfileId = GameProfile.VanillaId;

        string? message = null;

        if (legacyManifestExists || realModulesHasContent)
        {
            var defaultProfile = GameProfile.CreateUser(GetDefaultProfileName());
            AppPaths.EnsureProfileLayout(defaultProfile.Id);
            File.WriteAllText(
                AppPaths.ProfileMetaPath(defaultProfile.Id),
                JsonSerializer.Serialize(defaultProfile, JsonOptions));

            var modulesDest = AppPaths.ProfileModulesFolder(defaultProfile.Id);
            Directory.CreateDirectory(modulesDest);

            if (realModulesHasContent && gameModules is not null)
            {
                try
                {
                    MoveDirectoryContents(gameModules, modulesDest);
                    if (!Directory.EnumerateFileSystemEntries(gameModules).Any())
                        Directory.Delete(gameModules);

                    message = "Migrated existing Modules into a new profile overlay.";
                }
                catch (Exception ex)
                {
                    message = $"Migration incomplete: {ex.Message}";
                    settings.ActiveProfileId = GameProfile.VanillaId;
                    Save(settings);
                    return message;
                }
            }

            if (legacyManifestExists)
            {
                try
                {
                    var dest = AppPaths.ProfileInstalledModsPath(defaultProfile.Id);
                    if (!File.Exists(dest))
                        File.Move(AppPaths.LegacyInstalledModsPath, dest);
                    else
                        File.Delete(AppPaths.LegacyInstalledModsPath);
                }
                catch
                {
                    // best effort
                }
            }

            settings.ActiveProfileId = defaultProfile.Id;
            message ??= "Created default profile from previous install.";
        }

        Save(settings);

        try
        {
            if (!string.Equals(settings.ActiveProfileId, GameProfile.VanillaId, StringComparison.OrdinalIgnoreCase)
                && GamePathValidator.IsValid(settings.GamePath)
                && !GameProcessService.IsRunning(settings.GamePath))
            {
                var profilePath = AppPaths.ProfileMetaPath(settings.ActiveProfileId);
                GameProfile? profile = null;
                if (File.Exists(profilePath))
                {
                    var json = File.ReadAllText(profilePath);
                    profile = JsonSerializer.Deserialize<GameProfile>(json, JsonOptions);
                }

                profile ??= new GameProfile
                {
                    Id = settings.ActiveProfileId,
                    Name = settings.ActiveProfileId,
                };

                overlayService.Apply(profile, settings.GamePath);
            }
            else if (string.Equals(settings.ActiveProfileId, GameProfile.VanillaId, StringComparison.OrdinalIgnoreCase)
                     && GamePathValidator.IsValid(settings.GamePath))
            {
                overlayService.UnapplyAll(settings.GamePath, ignoreGameRunning: true);
            }
        }
        catch
        {
            // best-effort
        }

        return message;
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

    private static string GetDefaultProfileName()
    {
        try
        {
            return L.Get(I18n.ProfileKeys.DefaultName);
        }
        catch
        {
            return "Default";
        }
    }
}

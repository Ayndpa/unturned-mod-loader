using System.Text.Json;
using UnturnedModLoader.Models;

namespace UnturnedModLoader.Services;

public sealed class ProfileService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly SettingsService _settingsService;
    private readonly AppSettings _settings;
    private readonly VirtualFilesystemService _vfs;

    public ProfileService(
        SettingsService settingsService,
        AppSettings settings,
        VirtualFilesystemService vfs)
    {
        _settingsService = settingsService;
        _settings = settings;
        _vfs = vfs;
        AppPaths.EnsureAppData();
        Directory.CreateDirectory(AppPaths.ProfilesRoot);
    }

    public string ActiveProfileId => _settings.ActiveProfileId;

    public GameProfile GetActive()
    {
        EnsureAtLeastOneProfile();
        return GetById(ActiveProfileId) ?? List()[0];
    }

    public GameProfile? GetById(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        var path = AppPaths.ProfileMetaPath(id);
        if (!File.Exists(path))
            return null;

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<GameProfile>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public IReadOnlyList<GameProfile> List()
    {
        var list = new List<GameProfile>();

        if (!Directory.Exists(AppPaths.ProfilesRoot))
            return list;

        foreach (var dir in Directory.EnumerateDirectories(AppPaths.ProfilesRoot))
        {
            var id = Path.GetFileName(dir);
            // Legacy leftover folder from pre-profile layout.
            if (string.IsNullOrWhiteSpace(id)
                || string.Equals(id, "vanilla", StringComparison.OrdinalIgnoreCase))
                continue;

            var profile = GetById(id);
            if (profile is null)
            {
                profile = new GameProfile
                {
                    Id = id,
                    Name = id,
                    CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                };
                SaveMeta(profile);
            }

            list.Add(profile);
        }

        return list
            .OrderBy(p => p.CreatedAt)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public void EnsureAtLeastOneProfile()
    {
        MigrateLegacyVanillaActiveId();

        var profiles = List();
        if (profiles.Count == 0)
        {
            var created = Create(GetDefaultDisplayName());
            _settings.ActiveProfileId = created.Id;
            _settingsService.Save(_settings);
            return;
        }

        if (string.IsNullOrWhiteSpace(_settings.ActiveProfileId)
            || GetById(_settings.ActiveProfileId) is null)
        {
            _settings.ActiveProfileId = profiles[0].Id;
            _settingsService.Save(_settings);
        }
    }

    public GameProfile Create(string name)
    {
        var trimmed = string.IsNullOrWhiteSpace(name) ? GetDefaultDisplayName() : name.Trim();
        var profile = GameProfile.CreateUser(trimmed);
        AppPaths.EnsureProfileLayout(profile.Id);
        SaveMeta(profile);
        return profile;
    }

    public bool Rename(string id, string newName)
    {
        var profile = GetById(id);
        if (profile is null)
            return false;

        profile.Name = string.IsNullOrWhiteSpace(newName) ? profile.Name : newName.Trim();
        SaveMeta(profile);
        return true;
    }

    public MountResult Delete(string id)
    {
        var profile = GetById(id);
        if (profile is null)
            return MountResult.Fail("Profile not found.");

        var wasActive = string.Equals(ActiveProfileId, id, StringComparison.OrdinalIgnoreCase);
        var switchedAway = false;

        if (wasActive)
        {
            var next = List().FirstOrDefault(p => !string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
            if (next is not null)
            {
                var switchResult = SetActive(next.Id);
                if (!switchResult.Success)
                    return switchResult;
                switchedAway = true;
            }
            else
            {
                // Last profile: clear active before removing so Ensure can recreate.
                _settings.ActiveProfileId = "";
                _settingsService.Save(_settings);
            }
        }

        var dir = AppPaths.ProfileDir(id);
        if (Directory.Exists(dir))
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex)
            {
                return MountResult.Fail(ex.Message);
            }
        }

        EnsureAtLeastOneProfile();

        // Deleted the last profile and created a replacement — remount it.
        if (wasActive && !switchedAway)
        {
            var active = GetActive();
            if (GamePathValidator.IsValid(_settings.GamePath))
                return SetActive(active.Id);
        }

        return MountResult.Ok();
    }

    public MountResult SetActive(string id)
    {
        var profile = GetById(id);
        if (profile is null)
            return MountResult.Fail("Profile not found.");

        var gamePath = _settings.GamePath;
        if (!GamePathValidator.IsValid(gamePath))
            return MountResult.Fail("Configure a valid game path first.");

        AppPaths.EnsureProfileLayout(profile.Id);
        _vfs.SetLowerRoot(gamePath);
        _vfs.SetActiveOverlay(profile.Id);
        _settings.ActiveProfileId = profile.Id;
        _settingsService.Save(_settings);
        return MountResult.Ok();
    }

    public MountResult SyncActiveMounts()
    {
        EnsureAtLeastOneProfile();
        var profile = GetActive();
        var gamePath = _settings.GamePath;

        if (!GamePathValidator.IsValid(gamePath))
            return MountResult.Ok();

        if (GameProcessService.IsRunning(gamePath))
            return MountResult.Ok();

        _vfs.SetLowerRoot(gamePath);
        _vfs.SetActiveOverlay(profile.Id);
        return MountResult.Ok();
    }

    public void SaveMeta(GameProfile profile)
    {
        AppPaths.EnsureProfileLayout(profile.Id);
        var json = JsonSerializer.Serialize(profile, JsonOptions);
        File.WriteAllText(AppPaths.ProfileMetaPath(profile.Id), json);
    }

    private void MigrateLegacyVanillaActiveId()
    {
        if (!string.Equals(_settings.ActiveProfileId, "vanilla", StringComparison.OrdinalIgnoreCase))
            return;

        var userDirs = Directory.Exists(AppPaths.ProfilesRoot)
            ? Directory.EnumerateDirectories(AppPaths.ProfilesRoot)
                .Select(Path.GetFileName)
                .Where(id => !string.IsNullOrWhiteSpace(id)
                             && !string.Equals(id, "vanilla", StringComparison.OrdinalIgnoreCase))
                .ToList()
            : [];

        _settings.ActiveProfileId = userDirs.Count > 0 ? userDirs[0]! : "";
        _settingsService.Save(_settings);
    }

    private static string GetDefaultDisplayName()
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

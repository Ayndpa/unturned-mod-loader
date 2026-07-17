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

        if (string.Equals(id, GameProfile.DefaultBuiltInId, StringComparison.OrdinalIgnoreCase))
            return GameProfile.CreateBuiltInDefault(GetDefaultDisplayName());

        var path = AppPaths.ProfileMetaPath(id);
        if (!File.Exists(path))
            return null;

        try
        {
            var json = File.ReadAllText(path);
            var profile = JsonSerializer.Deserialize<GameProfile>(json, JsonOptions);
            if (profile is null)
                return null;

            profile.IsBuiltIn = profile.IsBuiltIn
                                || string.Equals(profile.Id, GameProfile.DefaultBuiltInId, StringComparison.OrdinalIgnoreCase);
            return profile;
        }
        catch
        {
            return null;
        }
    }

    public IReadOnlyList<GameProfile> List()
    {
        var list = new List<GameProfile>();

        var builtIn = GetById(GameProfile.DefaultBuiltInId);
        if (builtIn is not null)
            list.Add(builtIn);
        else if (Directory.Exists(Path.Combine(AppPaths.ProfilesRoot, GameProfile.DefaultBuiltInId)))
        {
            builtIn = GameProfile.CreateBuiltInDefault(GetDefaultDisplayName());
            SaveMeta(builtIn);
            list.Add(builtIn);
        }

        if (!Directory.Exists(AppPaths.ProfilesRoot))
            return list;

        foreach (var dir in Directory.EnumerateDirectories(AppPaths.ProfilesRoot))
        {
            var id = Path.GetFileName(dir);
            if (string.Equals(id, GameProfile.DefaultBuiltInId, StringComparison.OrdinalIgnoreCase)
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
            .OrderBy(p => p.IsBuiltIn ? 0 : 1)
            .ThenBy(p => p.CreatedAt)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public void EnsureAtLeastOneProfile()
    {
        MigrateLegacyVanillaActiveId();

        var profiles = List();
        if (profiles.Count == 0)
        {
            var created = Create(GetDefaultDisplayName(), asBuiltIn: true, id: GameProfile.DefaultBuiltInId);
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

    public GameProfile Create(string name, bool asBuiltIn = false, string? id = null)
    {
        var trimmed = string.IsNullOrWhiteSpace(name) ? GetDefaultDisplayName() : name.Trim();
        GameProfile profile;
        if (asBuiltIn && !string.IsNullOrWhiteSpace(id))
        {
            profile = new GameProfile
            {
                Id = id,
                Name = trimmed,
                IsBuiltIn = true,
                CreatedAt = 0,
            };
        }
        else
        {
            profile = GameProfile.CreateUser(trimmed);
        }

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

        if (profile.IsBuiltIn)
            return MountResult.Fail("Cannot delete the built-in profile.");

        if (List().Count <= 1)
            return MountResult.Fail("Cannot delete the last profile.");

        if (string.Equals(ActiveProfileId, id, StringComparison.OrdinalIgnoreCase))
        {
            var next = List().First(p => !string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
            var switchResult = SetActive(next.Id);
            if (!switchResult.Success)
                return switchResult;
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

        _settings.ActiveProfileId = userDirs.Count > 0 ? userDirs[0]! : GameProfile.DefaultBuiltInId;
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
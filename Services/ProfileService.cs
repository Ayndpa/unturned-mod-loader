using System.Text.Json;
using UnturnedModLoader.Models;

namespace UnturnedModLoader.Services;

public sealed class ProfileService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly SettingsService _settingsService;
    private readonly AppSettings _settings;
    private readonly GameOverlayService _overlayService;

    public ProfileService(
        SettingsService settingsService,
        AppSettings settings,
        GameOverlayService overlayService)
    {
        _settingsService = settingsService;
        _settings = settings;
        _overlayService = overlayService;
        AppPaths.EnsureAppData();
        Directory.CreateDirectory(AppPaths.ProfilesRoot);
    }

    public string ActiveProfileId =>
        string.IsNullOrWhiteSpace(_settings.ActiveProfileId)
            ? GameProfile.VanillaId
            : _settings.ActiveProfileId;

    public GameProfile GetActive() =>
        GetById(ActiveProfileId) ?? GameProfile.CreateVanilla(GetVanillaDisplayName());

    public GameProfile? GetById(string id)
    {
        if (string.Equals(id, GameProfile.VanillaId, StringComparison.OrdinalIgnoreCase))
            return GameProfile.CreateVanilla(GetVanillaDisplayName());

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
        var list = new List<GameProfile>
        {
            GameProfile.CreateVanilla(GetVanillaDisplayName()),
        };

        if (!Directory.Exists(AppPaths.ProfilesRoot))
            return list;

        foreach (var dir in Directory.EnumerateDirectories(AppPaths.ProfilesRoot))
        {
            var id = Path.GetFileName(dir);
            if (string.Equals(id, GameProfile.VanillaId, StringComparison.OrdinalIgnoreCase))
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
            .OrderBy(p => p.IsVanilla ? 0 : 1)
            .ThenBy(p => p.CreatedAt)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public GameProfile Create(string name)
    {
        var trimmed = string.IsNullOrWhiteSpace(name) ? "Profile" : name.Trim();
        var profile = GameProfile.CreateUser(trimmed);
        AppPaths.EnsureProfileLayout(profile.Id);
        SaveMeta(profile);
        return profile;
    }

    public bool Rename(string id, string newName)
    {
        if (string.Equals(id, GameProfile.VanillaId, StringComparison.OrdinalIgnoreCase))
            return false;

        var profile = GetById(id);
        if (profile is null)
            return false;

        profile.Name = string.IsNullOrWhiteSpace(newName) ? profile.Name : newName.Trim();
        SaveMeta(profile);
        return true;
    }

    public MountResult Delete(string id)
    {
        if (string.Equals(id, GameProfile.VanillaId, StringComparison.OrdinalIgnoreCase))
            return MountResult.Fail("Cannot delete the vanilla profile.");

        if (string.Equals(ActiveProfileId, id, StringComparison.OrdinalIgnoreCase))
        {
            var switchResult = SetActive(GameProfile.VanillaId);
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
        var profile = GetById(id) ?? (string.Equals(id, GameProfile.VanillaId, StringComparison.OrdinalIgnoreCase)
            ? GameProfile.CreateVanilla(GetVanillaDisplayName())
            : null);

        if (profile is null)
            return MountResult.Fail("Profile not found.");

        var gamePath = _settings.GamePath;
        MountResult mountResult;
        if (profile.IsVanilla)
        {
            mountResult = string.IsNullOrWhiteSpace(gamePath)
                ? MountResult.Ok()
                : _overlayService.UnapplyAll(gamePath);
        }
        else
        {
            if (!GamePathValidator.IsValid(gamePath))
                return MountResult.Fail("Configure a valid game path first.");

            AppPaths.EnsureProfileLayout(profile.Id);
            mountResult = _overlayService.Apply(profile, gamePath);
        }

        if (!mountResult.Success)
            return mountResult;

        _settings.ActiveProfileId = profile.Id;
        _settingsService.Save(_settings);
        return MountResult.Ok();
    }

    /// <summary>Repair overlay for the current active profile (startup).</summary>
    public MountResult SyncActiveMounts()
    {
        var profile = GetActive();
        var gamePath = _settings.GamePath;

        if (profile.IsVanilla)
        {
            if (GamePathValidator.IsValid(gamePath))
                return _overlayService.UnapplyAll(gamePath, ignoreGameRunning: true);
            return MountResult.Ok();
        }

        if (!GamePathValidator.IsValid(gamePath))
            return MountResult.Ok();

        if (GameProcessService.IsRunning(gamePath))
            return MountResult.Ok();

        return _overlayService.Apply(profile, gamePath);
    }

    public void SaveMeta(GameProfile profile)
    {
        if (profile.IsVanilla)
            return;

        AppPaths.EnsureProfileLayout(profile.Id);
        var json = JsonSerializer.Serialize(profile, JsonOptions);
        File.WriteAllText(AppPaths.ProfileMetaPath(profile.Id), json);
    }

    private static string GetVanillaDisplayName()
    {
        try
        {
            return L.Get(I18n.ProfileKeys.VanillaName);
        }
        catch
        {
            return "Vanilla";
        }
    }
}

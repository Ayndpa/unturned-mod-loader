using System.Text.Json;
using UnturnedModLoader.Models;

namespace UnturnedModLoader.Services;

public sealed class ProfileMountService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public MountState LoadState()
    {
        AppPaths.EnsureAppData();
        if (!File.Exists(AppPaths.MountStatePath))
            return new MountState();

        try
        {
            var json = File.ReadAllText(AppPaths.MountStatePath);
            return JsonSerializer.Deserialize<MountState>(json, JsonOptions) ?? new MountState();
        }
        catch
        {
            return new MountState();
        }
    }

    public void SaveState(MountState state)
    {
        AppPaths.EnsureAppData();
        var json = JsonSerializer.Serialize(state, JsonOptions);
        File.WriteAllText(AppPaths.MountStatePath, json);
    }

    public MountResult EnsureApplied(GameProfile profile, string gamePath)
    {
        if (!GamePathValidator.IsValid(gamePath))
            return MountResult.Fail("Game path is not valid.");

        if (GameProcessService.IsRunning(gamePath))
            return MountResult.Fail("Game is running.");

        return Apply(profile, gamePath);
    }

    public MountResult Apply(GameProfile profile, string gamePath)
    {
        if (!OperatingSystem.IsWindows())
            return MountResult.Fail("Profile mounts require Windows.");

        if (!GamePathValidator.IsValid(gamePath))
            return MountResult.Fail("Game path is not valid.");

        if (GameProcessService.IsRunning(gamePath))
            return MountResult.Fail("Game is running.");

        var previous = LoadState();
        if (!string.IsNullOrWhiteSpace(previous.GamePath) && !PathsEqual(previous.GamePath, gamePath))
            Unapply(previous.GamePath!, ignoreGameRunning: true);

        var unapply = Unapply(gamePath, ignoreGameRunning: false);
        if (!unapply.Success)
            return unapply;

        AppPaths.EnsureProfileLayout(profile.Id);
        var applied = new List<AppliedMount>();
        var fullGamePath = Path.GetFullPath(gamePath);

        try
        {
            foreach (var mount in MountPointDefinition.EnabledV1)
            {
                // Prefer overlay layout; fall back to legacy mounts\ path mapping.
                var source = Path.GetFullPath(
                    Path.Combine(AppPaths.ProfileOverlayDir(profile.Id), mount.RelativeGamePath));
                Directory.CreateDirectory(source);

                var target = Path.Combine(fullGamePath, mount.RelativeGamePath);
                var prepare = PrepareMountTarget(target, source);
                if (!prepare.Success)
                {
                    RollbackGameMounts(fullGamePath, applied);
                    return prepare;
                }

                if (!JunctionHelper.IsJunction(target))
                    JunctionHelper.CreateJunction(target, source);

                applied.Add(new AppliedMount
                {
                    MountId = mount.Id,
                    GameRelativePath = mount.RelativeGamePath,
                    SourcePath = source,
                    Kind = "Junction",
                });
            }

            SaveState(new MountState
            {
                AppliedProfileId = profile.Id,
                GamePath = fullGamePath,
                Mounts = applied,
            });

            return MountResult.Ok();
        }
        catch (Exception ex)
        {
            RollbackGameMounts(fullGamePath, applied);
            return MountResult.Fail(ex.Message);
        }
    }

    public MountResult Unapply(string gamePath, bool ignoreGameRunning = false)
    {
        if (!OperatingSystem.IsWindows())
            return MountResult.Ok();

        if (!ignoreGameRunning && GamePathValidator.IsValid(gamePath) && GameProcessService.IsRunning(gamePath))
            return MountResult.Fail("Game is running.");

        var state = LoadState();
        var errors = new List<string>();
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var mount in MountPointDefinition.EnabledV1)
            targets.Add(Path.Combine(gamePath, mount.RelativeGamePath));

        if (!string.IsNullOrWhiteSpace(state.GamePath))
        {
            foreach (var mount in state.Mounts)
                targets.Add(Path.Combine(state.GamePath, mount.GameRelativePath));
        }

        foreach (var path in targets)
        {
            try
            {
                TryRemoveOwnedJunction(path);
            }
            catch (Exception ex)
            {
                errors.Add(ex.Message);
            }
        }

        // Clear state when it refers to this game path or is empty.
        if (string.IsNullOrWhiteSpace(state.GamePath) || PathsEqual(state.GamePath, gamePath))
            SaveState(new MountState());

        return errors.Count == 0
            ? MountResult.Ok()
            : MountResult.Fail(string.Join("; ", errors));
    }

    public bool IsOwnedJunction(string path)
    {
        if (!JunctionHelper.IsJunction(path))
            return false;

        if (!JunctionHelper.TryGetJunctionTarget(path, out var target) || string.IsNullOrWhiteSpace(target))
            return false;

        try
        {
            var profilesRoot = Path.GetFullPath(AppPaths.ProfilesRoot);
            var fullTarget = Path.GetFullPath(target);
            return fullTarget.StartsWith(profilesRoot.TrimEnd('\\') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                   || PathsEqual(fullTarget, profilesRoot);
        }
        catch
        {
            return false;
        }
    }

    private MountResult PrepareMountTarget(string target, string source)
    {
        if (!Directory.Exists(target) && !File.Exists(target))
            return MountResult.Ok();

        if (JunctionHelper.IsJunction(target))
        {
            if (JunctionHelper.TryGetJunctionTarget(target, out var existing) &&
                existing is not null &&
                PathsEqual(existing, source))
            {
                return MountResult.Ok();
            }

            try
            {
                JunctionHelper.DeleteJunction(target);
                return MountResult.Ok();
            }
            catch (Exception ex)
            {
                return MountResult.Fail(ex.Message);
            }
        }

        if (Directory.Exists(target))
        {
            if (Directory.EnumerateFileSystemEntries(target).Any())
            {
                return MountResult.Fail(
                    $"Game folder '{Path.GetFileName(target)}' already contains files. Migrate or clear it before mounting.");
            }

            try
            {
                Directory.Delete(target);
                return MountResult.Ok();
            }
            catch (Exception ex)
            {
                return MountResult.Fail(ex.Message);
            }
        }

        return MountResult.Fail($"Cannot replace file at mount target: {target}");
    }

    private void TryRemoveOwnedJunction(string path)
    {
        if (!Directory.Exists(path))
            return;

        if (!JunctionHelper.IsJunction(path))
            return;

        if (!IsOwnedJunction(path))
            return;

        JunctionHelper.DeleteJunction(path);
    }

    private void RollbackGameMounts(string gamePath, List<AppliedMount> applied)
    {
        foreach (var mount in applied)
        {
            try
            {
                var path = Path.Combine(gamePath, mount.GameRelativePath);
                TryRemoveOwnedJunction(path);
            }
            catch
            {
                // ignore
            }
        }
    }

    private static bool PathsEqual(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(a).TrimEnd('\\', '/'),
                Path.GetFullPath(b).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }
}

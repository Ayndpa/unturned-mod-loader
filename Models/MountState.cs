namespace UnturnedModLoader.Models;

public class MountState
{
    public string? AppliedProfileId { get; set; }
    public string? GamePath { get; set; }
    public List<AppliedMount> Mounts { get; set; } = [];
}

public class AppliedMount
{
    public string MountId { get; set; } = "";
    public string GameRelativePath { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string Kind { get; set; } = "Junction";
}

public sealed class MountPointDefinition
{
    public required string Id { get; init; }
    public required string RelativeGamePath { get; init; }
    public required string RelativeProfilePath { get; init; }
    public bool EnabledInV1 { get; init; }

    public static IReadOnlyList<MountPointDefinition> Registry { get; } =
    [
        new MountPointDefinition
        {
            Id = "modules",
            RelativeGamePath = "Modules",
            RelativeProfilePath = Path.Combine("mounts", "Modules"),
            EnabledInV1 = true,
        },
        new MountPointDefinition
        {
            Id = "bepinex",
            RelativeGamePath = "BepInEx",
            RelativeProfilePath = Path.Combine("mounts", "BepInEx"),
            EnabledInV1 = false,
        },
    ];

    public static IEnumerable<MountPointDefinition> EnabledV1 =>
        Registry.Where(m => m.EnabledInV1);
}

public sealed class MountResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }

    public static MountResult Ok() => new() { Success = true };
    public static MountResult Fail(string error) => new() { Success = false, Error = error };
}

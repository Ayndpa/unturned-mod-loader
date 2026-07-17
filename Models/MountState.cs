namespace UnturnedModLoader.Models;

/// <summary>
/// Shared result type for overlay/profile mount operations (SetActive, SyncActiveMounts, etc.).
/// </summary>
public sealed class MountResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }

    public static MountResult Ok() => new() { Success = true };
    public static MountResult Fail(string error) => new() { Success = false, Error = error };
}

namespace UnturnedModLoader.Models.Api;

public record ModsListResult(
    bool Success,
    IReadOnlyList<RemoteMod> Mods,
    int Total,
    string? Error);
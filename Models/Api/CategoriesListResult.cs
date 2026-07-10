namespace UnturnedModLoader.Models.Api;

public record CategoriesListResult(
    bool Success,
    IReadOnlyList<RemoteCategory> Categories,
    string? Error);
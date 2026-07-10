using UnturnedModLoader.Models.Api;

namespace UnturnedModLoader.Services.Api;

public interface IModsApiClient
{
    string BaseUrl { get; }
    Task<CategoriesListResult> GetCategoriesAsync(CancellationToken cancellationToken = default);
    Task<ModsListResult> GetModsAsync(ModsQuery query, CancellationToken cancellationToken = default);
}
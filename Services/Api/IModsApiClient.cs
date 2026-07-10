using UnturnedModLoader.Models.Api;

namespace UnturnedModLoader.Services.Api;

public interface IModsApiClient
{
    string BaseUrl { get; }
    Task<ModsListResult> GetModsAsync(ModsQuery query, CancellationToken cancellationToken = default);
}
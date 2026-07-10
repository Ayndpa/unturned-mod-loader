using UnturnedModLoader.Models.Api;

namespace UnturnedModLoader.Services.Api;

public interface IAuthApiClient
{
    Task<AuthResult> LoginAsync(string username, string password, CancellationToken cancellationToken = default);

    Task<AuthResult> RegisterAsync(
        string username,
        string email,
        string password,
        CancellationToken cancellationToken = default);

    Task<AuthResult> MeAsync(CancellationToken cancellationToken = default);

    Task<AuthActionResult> LogoutAsync(CancellationToken cancellationToken = default);
}
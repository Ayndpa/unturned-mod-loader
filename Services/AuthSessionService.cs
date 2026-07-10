using UnturnedModLoader.Models;
using UnturnedModLoader.Models.Api;
using UnturnedModLoader.Services.Api;

namespace UnturnedModLoader.Services;

public sealed class AuthSessionService(
    ApiClientBundle api,
    SettingsService settingsService,
    AppSettings settings)
{
    public async Task<bool> TryRestoreSessionAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(settings.AuthToken))
            return false;

        var result = await api.Auth.MeAsync(cancellationToken);
        if (!result.Success || result.User is null)
        {
            ClearLocalSession();
            return false;
        }

        api.SaveSessionToSettings(settings, result.User);
        settingsService.Save(settings);
        return true;
    }

    public async Task<AuthResult> LoginAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        var result = await api.Auth.LoginAsync(username, password, cancellationToken);
        if (!result.Success || result.User is null)
            return result;

        await RefreshProfileAsync(cancellationToken);
        PersistSession();
        return result;
    }

    public async Task<AuthResult> RegisterAsync(
        string username,
        string email,
        string password,
        CancellationToken cancellationToken = default)
    {
        var result = await api.Auth.RegisterAsync(username, email, password, cancellationToken);
        if (!result.Success || result.User is null)
            return result;

        await RefreshProfileAsync(cancellationToken);
        PersistSession();
        return result;
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        await api.Auth.LogoutAsync(cancellationToken);
        ClearLocalSession();
    }

    public async Task<AuthUser?> GetCurrentUserAsync(CancellationToken cancellationToken = default)
    {
        var result = await api.Auth.MeAsync(cancellationToken);
        if (!result.Success || result.User is null)
            return null;

        api.SaveSessionToSettings(settings, result.User);
        settingsService.Save(settings);
        return result.User;
    }

    private async Task RefreshProfileAsync(CancellationToken cancellationToken)
    {
        var me = await api.Auth.MeAsync(cancellationToken);
        if (me.Success && me.User is not null)
            api.SaveSessionToSettings(settings, me.User);
    }

    private void PersistSession()
    {
        api.SaveSessionToSettings(settings);
        settingsService.Save(settings);
    }

    private void ClearLocalSession()
    {
        api.ClearSession(settings);
        settingsService.Save(settings);
    }
}
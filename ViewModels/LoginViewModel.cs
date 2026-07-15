using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnturnedModLoader.I18n;
using UnturnedModLoader.Models;
using UnturnedModLoader.Services;
using UnturnedModLoader.Services.Api;

namespace UnturnedModLoader.ViewModels;

public partial class LoginViewModel : ViewModelBase
{
    private readonly AuthSessionService _session;
    private readonly AppSettings _settings;

    private const string SuccessHtml = @"
<!DOCTYPE html>
<html>
<head>
  <meta charset=""utf-8"">
  <title>Authentication Successful</title>
  <style>
    body { font-family: -apple-system, BlinkMacSystemFont, ""Segoe UI"", Roboto, Helvetica, Arial, sans-serif; background-color: #0d1117; color: #c9d1d9; display: flex; align-items: center; justify-content: center; height: 100vh; margin: 0; }
    .card { background-color: #161b22; border: 1px solid #30363d; border-radius: 8px; padding: 32px; text-align: center; max-width: 400px; box-shadow: 0 4px 12px rgba(0,0,0,0.3); }
    h1 { color: #2ea44f; margin-top: 0; font-size: 24px; }
    p { margin-bottom: 0px; line-height: 1.5; color: #8b949e; }
  </style>
</head>
<body>
  <div class=""card"">
    <h1>✓ Authenticated Successfully</h1>
    <p>You have successfully logged in to Unturned Mods Hub. You can now close this tab and return to the Mod Loader application.</p>
  </div>
</body>
</html>
";

    [ObservableProperty]
    private string _errorMessage = "";

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isCheckingSession;

    public string BusyText => IsCheckingSession
        ? L.Get(Login.RestoringSession)
        : L.Get(Login.LoggingIn);

    public event Action<AppSettings>? LoggedIn;

    public LoginViewModel(AuthSessionService session, AppSettings settings)
    {
        _session = session;
        _settings = settings;
        _ = TryRestoreSessionAsync();
    }

    [RelayCommand]
    private async Task LoginViaBrowserAsync()
    {
        IsBusy = true;
        HasError = false;

        using var listener = new HttpListener();
        const int port = 52026;
        listener.Prefixes.Add($"http://localhost:{port}/");

        try
        {
            listener.Start();
        }
        catch (Exception ex)
        {
            ShowError($"Failed to start local listener on port {port}: {ex.Message}");
            IsBusy = false;
            return;
        }

        try
        {
            var baseUrl = ModsApiClientFactory.ResolveBaseUrl(_settings).TrimEnd('/');
            var loginUrl = $"{baseUrl}/login?next=/api/auth/cli-login?port={port}";
            OpenUrl(loginUrl);

            var context = await listener.GetContextAsync();
            var request = context.Request;
            var response = context.Response;

            var token = request.QueryString["token"];
            if (string.IsNullOrWhiteSpace(token))
            {
                ShowError("No token received from web page.");
                response.StatusCode = 400;
                using (var writer = new StreamWriter(response.OutputStream))
                {
                    await writer.WriteAsync("Authentication failed: No token received.");
                }
                response.Close();
                return;
            }

            response.StatusCode = 200;
            response.ContentType = "text/html; charset=utf-8";
            using (var writer = new StreamWriter(response.OutputStream))
            {
                await writer.WriteAsync(SuccessHtml);
            }
            response.Close();

            _session.SaveToken(token);
            var success = await _session.TryRestoreSessionAsync();
            if (success)
            {
                LoggedIn?.Invoke(_settings);
            }
            else
            {
                ShowError("Failed to restore session after web login.");
            }
        }
        catch (Exception ex)
        {
            ShowError($"Error during web login: {ex.Message}");
        }
        finally
        {
            try { listener.Stop(); } catch {}
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void GoToRegister()
    {
        var baseUrl = ModsApiClientFactory.ResolveBaseUrl(_settings).TrimEnd('/');
        OpenUrl($"{baseUrl}/register");
    }

    private async Task TryRestoreSessionAsync()
    {
        if (!_settings.IsLoggedIn)
            return;

        IsCheckingSession = true;
        IsBusy = true;

        try
        {
            if (await _session.TryRestoreSessionAsync())
                LoggedIn?.Invoke(_settings);
        }
        finally
        {
            IsCheckingSession = false;
            IsBusy = false;
        }
    }

    private static void OpenUrl(string url)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            Process.Start("open", url);
        }
        else
        {
            Process.Start("xdg-open", url);
        }
    }

    partial void OnIsCheckingSessionChanged(bool value) =>
        OnPropertyChanged(nameof(BusyText));

    partial void OnIsBusyChanged(bool value) =>
        OnPropertyChanged(nameof(BusyText));

    protected override void OnLocalizationChanged() =>
        OnPropertyChanged(nameof(BusyText));

    private void ShowError(string message)
    {
        ErrorMessage = message;
        HasError = true;
    }
}
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnturnedModLoader.Models;
using UnturnedModLoader.Services;
using UnturnedModLoader.Services.Api;

namespace UnturnedModLoader.ViewModels;

public partial class LoginViewModel : ViewModelBase
{
    private readonly AuthSessionService _session;
    private readonly AppSettings _settings;

    [ObservableProperty]
    private string _username = "";

    [ObservableProperty]
    private string _password = "";

    [ObservableProperty]
    private string _errorMessage = "";

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isCheckingSession;

    public string BusyText => IsCheckingSession ? "正在恢复登录状态…" : "登录中…";

    public event Action<AppSettings>? LoggedIn;
    public event Action? RegisterRequested;

    public LoginViewModel(AuthSessionService session, AppSettings settings)
    {
        _session = session;
        _settings = settings;
        _ = TryRestoreSessionAsync();
    }

    [RelayCommand]
    private async Task LoginAsync()
    {
        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password))
        {
            ShowError("请填写用户名和密码。");
            return;
        }

        IsBusy = true;
        HasError = false;

        try
        {
            var result = await _session.LoginAsync(Username.Trim(), Password);
            if (!result.Success)
            {
                ShowError(result.Error ?? "登录失败，请检查用户名和密码。");
                return;
            }

            LoggedIn?.Invoke(_settings);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void GoToRegister() => RegisterRequested?.Invoke();

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

    partial void OnIsCheckingSessionChanged(bool value) =>
        OnPropertyChanged(nameof(BusyText));

    partial void OnIsBusyChanged(bool value) =>
        OnPropertyChanged(nameof(BusyText));

    private void ShowError(string message)
    {
        ErrorMessage = message;
        HasError = true;
    }
}
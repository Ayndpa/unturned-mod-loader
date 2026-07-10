using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnturnedModLoader.Models;
using UnturnedModLoader.Services;

namespace UnturnedModLoader.ViewModels;

public partial class RegisterViewModel : ViewModelBase
{
    private static readonly Regex UsernamePattern = new("^[a-zA-Z0-9_]{3,32}$", RegexOptions.Compiled);
    private static readonly Regex EmailPattern = new(@"^[^\s@]+@[^\s@]+\.[^\s@]+$", RegexOptions.Compiled);

    private readonly AuthSessionService _session;
    private readonly AppSettings _settings;

    [ObservableProperty]
    private string _username = "";

    [ObservableProperty]
    private string _email = "";

    [ObservableProperty]
    private string _password = "";

    [ObservableProperty]
    private string _confirmPassword = "";

    [ObservableProperty]
    private string _errorMessage = "";

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private bool _isBusy;

    public event Action<AppSettings>? Registered;
    public event Action? LoginRequested;

    public RegisterViewModel(AuthSessionService session, AppSettings settings)
    {
        _session = session;
        _settings = settings;
    }

    [RelayCommand]
    private async Task RegisterAsync()
    {
        var validationError = Validate();
        if (validationError is not null)
        {
            ShowError(validationError);
            return;
        }

        IsBusy = true;
        HasError = false;

        try
        {
            var result = await _session.RegisterAsync(
                Username.Trim(),
                Email.Trim(),
                Password);

            if (!result.Success)
            {
                ShowError(result.Error ?? "注册失败，请稍后重试。");
                return;
            }

            Registered?.Invoke(_settings);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void GoToLogin() => LoginRequested?.Invoke();

    private string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Username))
            return "请填写用户名。";

        if (!UsernamePattern.IsMatch(Username.Trim()))
            return "用户名需为 3–32 位字母、数字或下划线。";

        if (string.IsNullOrWhiteSpace(Email))
            return "请填写邮箱。";

        if (!EmailPattern.IsMatch(Email.Trim()))
            return "邮箱格式不正确。";

        if (string.IsNullOrWhiteSpace(Password))
            return "请填写密码。";

        if (Password.Length < 6)
            return "密码至少 6 位。";

        if (Password != ConfirmPassword)
            return "两次输入的密码不一致。";

        return null;
    }

    private void ShowError(string message)
    {
        ErrorMessage = message;
        HasError = true;
    }
}
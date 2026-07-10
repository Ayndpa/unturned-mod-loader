using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnturnedModLoader.I18n;
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
                ShowError(result.Error ?? L.Get(Register.Failed));
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
            return L.Get(Register.UsernameRequired);

        if (!UsernamePattern.IsMatch(Username.Trim()))
            return L.Get(Register.UsernameInvalid);

        if (string.IsNullOrWhiteSpace(Email))
            return L.Get(Register.EmailRequired);

        if (!EmailPattern.IsMatch(Email.Trim()))
            return L.Get(Register.EmailInvalid);

        if (string.IsNullOrWhiteSpace(Password))
            return L.Get(Register.PasswordRequired);

        if (Password.Length < 6)
            return L.Get(Register.PasswordTooShort);

        if (Password != ConfirmPassword)
            return L.Get(Register.PasswordMismatch);

        return null;
    }

    private void ShowError(string message)
    {
        ErrorMessage = message;
        HasError = true;
    }
}
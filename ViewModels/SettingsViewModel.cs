using System.Collections.ObjectModel;
using System.Runtime.Versioning;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnturnedModLoader.I18n;
using UnturnedModLoader.Models;
using UnturnedModLoader.Services;
using UnturnedModLoader.Views;

namespace UnturnedModLoader.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly SettingsService _settingsService;
    private readonly AppSettings _settings;
    private readonly FolderPickerService _folderPicker;
    private readonly AuthSessionService _session;
    private readonly ProfileService _profileService;
    private readonly GameOverlayService _overlayService;
    private readonly Window _owner;
    private string _currentRole = "";

    [ObservableProperty]
    private string _selectedSection = "game";

    [ObservableProperty]
    private string _gamePath = "";

    [ObservableProperty]
    private string _gamePathStatus = "";

    [ObservableProperty]
    private bool _isPathValid;

    [ObservableProperty]
    private bool _isDetecting;

    [ObservableProperty]
    private string _username = "";

    [ObservableProperty]
    private string _email = "";

    [ObservableProperty]
    private string _roleLabel = "";

    [ObservableProperty]
    private bool _isAccountLoading;

    [ObservableProperty]
    private string _accountStatus = "";

    [ObservableProperty]
    private string _selectedLocale = "zh";

    [ObservableProperty]
    private string _selectedTheme = "system";

    [ObservableProperty]
    private string _profileStatus = "";

    [ObservableProperty]
    private string _newProfileName = "";

    public bool IsLoggedIn => _settings.IsLoggedIn;
    public IReadOnlyList<string> LocaleOptions { get; } = ["zh", "en"];
    public bool IsZhLocaleSelected => SelectedLocale == "zh";
    public bool IsEnLocaleSelected => SelectedLocale == "en";
    public bool IsLightThemeSelected => SelectedTheme == "light";
    public bool IsDarkThemeSelected => SelectedTheme == "dark";
    public bool IsSystemThemeSelected => SelectedTheme == "system";
    public bool IsGameSection => SelectedSection == "game";
    public bool IsProfilesSection => SelectedSection == "profiles";
    public bool IsAccountSection => SelectedSection == "account";
    public bool IsAppearanceSection => SelectedSection == "appearance";

    public ObservableCollection<ProfileItemViewModel> Profiles { get; } = [];

    public event Action? CloseRequested;

    public SettingsViewModel(
        SettingsService settingsService,
        AppSettings settings,
        FolderPickerService folderPicker,
        AuthSessionService session,
        ProfileService profileService,
        GameOverlayService overlayService,
        Window owner,
        string? initialSection = null)
    {
        _settingsService = settingsService;
        _settings = settings;
        _folderPicker = folderPicker;
        _session = session;
        _profileService = profileService;
        _overlayService = overlayService;
        _owner = owner;

        _gamePath = settings.GamePath;
        _username = settings.Username ?? "";
        _selectedLocale = string.IsNullOrWhiteSpace(settings.Locale)
            ? LocalizationService.DetectDefaultLocaleCode()
            : settings.Locale;
        _selectedTheme = ThemeService.NormalizePreference(settings.Theme);
        _newProfileName = L.Get(ProfileKeys.NewName);

        if (!string.IsNullOrWhiteSpace(initialSection))
            _selectedSection = initialSection;

        UpdateGamePathStatus();
        RefreshProfiles();

        if (IsLoggedIn)
            _ = LoadAccountAsync();
    }

    public string GetLocaleLabel(string localeCode) => localeCode switch
    {
        "zh" => L.Get(LocaleKeys.Zh),
        "en" => L.Get(LocaleKeys.En),
        _ => localeCode,
    };

    [RelayCommand]
    private void SelectSection(string section)
    {
        if (string.IsNullOrWhiteSpace(section))
            return;

        SelectedSection = section;
    }

    [RelayCommand]
    private void SelectLocale(string localeCode)
    {
        if (localeCode is not ("zh" or "en") || localeCode == SelectedLocale)
            return;

        SelectedLocale = localeCode;
        LocalizationService.ApplyLocale(localeCode, _settings, _settingsService);
    }

    [RelayCommand]
    private void SelectTheme(string theme)
    {
        if (theme is not ("light" or "dark" or "system") || theme == SelectedTheme)
            return;

        SelectedTheme = theme;
        ThemeService.ApplyPreference(theme, _settings, _settingsService);
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();

    [RelayCommand]
    private async Task AutoDetectGamePathAsync()
    {
        if (IsDetecting)
            return;

        IsDetecting = true;
        GamePathStatus = L.Get(GamePathKeys.Detecting);

        try
        {
            SteamDetectionResult result;
            if (OperatingSystem.IsWindows())
                result = await Task.Run(DetectOnWindows);
            else
                result = new(false, null, L.Get(Steam.WindowsOnly));

            if (result.Success && result.GamePath is not null)
                GamePath = result.GamePath;

            GamePathStatus = result.Message;
        }
        finally
        {
            IsDetecting = false;
        }
    }

    [RelayCommand]
    private async Task BrowseGamePathAsync()
    {
        var picked = await _folderPicker.PickFolderAsync(
            L.Get(GamePathKeys.PickerTitle),
            string.IsNullOrWhiteSpace(GamePath) ? null : GamePath);

        if (picked is null)
            return;

        GamePath = picked;
    }

    [RelayCommand]
    private void CreateProfile()
    {
        try
        {
            var name = string.IsNullOrWhiteSpace(NewProfileName)
                ? L.Get(ProfileKeys.NewName)
                : NewProfileName.Trim();
            _profileService.Create(name);
            NewProfileName = L.Get(ProfileKeys.NewName);
            ProfileStatus = "";
            RefreshProfiles();
        }
        catch (Exception ex)
        {
            ProfileStatus = L.Get(ProfileKeys.CreateFailed) + " " + ex.Message;
        }
    }

    [RelayCommand]
    private void ActivateProfile(ProfileItemViewModel? profile)
    {
        if (profile is null)
            return;

        if (GameProcessService.IsRunning(_settings.GamePath))
        {
            ProfileStatus = L.Get(ProfileKeys.GameRunning);
            return;
        }

        var result = _profileService.SetActive(profile.Id);
        ProfileStatus = result.Success
            ? ""
            : L.Get(ProfileKeys.SwitchFailed, result.Error ?? "");
        RefreshProfiles();
    }

    [RelayCommand]
    private void DeleteProfile(ProfileItemViewModel? profile)
    {
        if (profile is null || profile.IsBuiltIn || profile.IsVanilla)
            return;

        if (GameProcessService.IsRunning(_settings.GamePath))
        {
            ProfileStatus = L.Get(ProfileKeys.GameRunning);
            return;
        }

        var result = _profileService.Delete(profile.Id);
        ProfileStatus = result.Success
            ? ""
            : L.Get(ProfileKeys.DeleteFailed, result.Error ?? "");
        RefreshProfiles();
    }

    [RelayCommand]
    private void OpenProfileFolder(ProfileItemViewModel? profile)
    {
        if (profile is null || profile.IsVanilla)
            return;

        try
        {
            var dir = AppPaths.ProfileDir(profile.Id);
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ProfileStatus = ex.Message;
        }
    }

    [RelayCommand]
    private async Task RefreshAccountAsync() => await LoadAccountAsync();

    [RelayCommand]
    private async Task LogoutAsync()
    {
        IsAccountLoading = true;
        AccountStatus = L.Get(Settings.LoggingOut);

        try
        {
            await _session.LogoutAsync();
            Username = "";
            Email = "";
            RoleLabel = "";
            _currentRole = "";
            AccountStatus = "";
            NotifyLoginState();
        }
        finally
        {
            IsAccountLoading = false;
        }
    }

    [RelayCommand]
    private async Task OpenLoginAsync()
    {
        var loginWindow = new LoginWindow();
        var viewModel = new LoginViewModel(_session, _settings);
        var loggedIn = false;

        viewModel.LoggedIn += _ =>
        {
            loggedIn = true;
            loginWindow.Close();
        };
        viewModel.RegisterRequested += () =>
        {
            loginWindow.Close();
            _ = OpenRegisterAsync();
        };

        loginWindow.DataContext = viewModel;
        await loginWindow.ShowDialog(_owner);

        if (loggedIn)
        {
            Username = _settings.Username ?? "";
            NotifyLoginState();
            await LoadAccountAsync();
        }
    }

    [RelayCommand]
    private async Task OpenRegisterAsync()
    {
        var registerWindow = new RegisterWindow();
        var viewModel = new RegisterViewModel(_session, _settings);
        var registered = false;

        viewModel.Registered += _ =>
        {
            registered = true;
            registerWindow.Close();
        };
        viewModel.LoginRequested += () => registerWindow.Close();

        registerWindow.DataContext = viewModel;
        await registerWindow.ShowDialog(_owner);

        if (registered)
        {
            Username = _settings.Username ?? "";
            NotifyLoginState();
            await LoadAccountAsync();
        }
    }

    private void RefreshProfiles()
    {
        Profiles.Clear();
        var activeId = _profileService.ActiveProfileId;
        foreach (var profile in _profileService.List())
            Profiles.Add(ProfileItemViewModel.From(profile, activeId));
    }

    private async Task LoadAccountAsync()
    {
        if (!IsLoggedIn)
            return;

        IsAccountLoading = true;
        AccountStatus = "";

        try
        {
            var result = await _session.GetCurrentUserAsync();
            if (result is null)
            {
                AccountStatus = L.Get(Settings.AccountLoadFailed);
                Username = _settings.Username ?? "";
                Email = "";
                RoleLabel = "";
                return;
            }

            Username = result.Username;
            Email = result.Email;
            _currentRole = result.Role;
            RoleLabel = MapRoleLabel(_currentRole);
            AccountStatus = L.Get(Settings.AccountSynced);
        }
        finally
        {
            IsAccountLoading = false;
        }
    }

    private void NotifyLoginState()
    {
        OnPropertyChanged(nameof(IsLoggedIn));
    }

    private void PersistGamePath()
    {
        if (!IsPathValid && !string.IsNullOrWhiteSpace(GamePath))
            return;

        var previous = _settings.GamePath;
        _settings.GamePath = GamePath;
        _settingsService.Save(_settings);

        if (IsPathValid &&
            !string.Equals(previous, GamePath, StringComparison.OrdinalIgnoreCase) &&
            !GameProcessService.IsRunning(GamePath))
        {
            // Tear down overlay on the previous install, then apply to the new path.
            if (!string.IsNullOrWhiteSpace(previous) && GamePathValidator.IsValid(previous))
                _overlayService.UnapplyAll(previous, ignoreGameRunning: true);

            _profileService.SyncActiveMounts();
        }
    }

    private void UpdateGamePathStatus()
    {
        IsPathValid = GamePathValidator.IsValid(GamePath);

        GamePathStatus = IsPathValid
            ? L.Get(GamePathKeys.Valid)
            : string.IsNullOrWhiteSpace(GamePath)
                ? L.Get(GamePathKeys.NotConfigured)
                : L.Get(GamePathKeys.Invalid, GamePathValidator.ExecutableName);
    }

    private static string MapRoleLabel(string role) => role switch
    {
        "admin" => L.Get(Role.Admin),
        "moderator" => L.Get(Role.Moderator),
        _ => L.Get(Role.User),
    };

    partial void OnSelectedLocaleChanged(string value)
    {
        OnPropertyChanged(nameof(IsZhLocaleSelected));
        OnPropertyChanged(nameof(IsEnLocaleSelected));
    }

    partial void OnSelectedThemeChanged(string value)
    {
        OnPropertyChanged(nameof(IsLightThemeSelected));
        OnPropertyChanged(nameof(IsDarkThemeSelected));
        OnPropertyChanged(nameof(IsSystemThemeSelected));
    }

    partial void OnSelectedSectionChanged(string value)
    {
        OnPropertyChanged(nameof(IsGameSection));
        OnPropertyChanged(nameof(IsProfilesSection));
        OnPropertyChanged(nameof(IsAccountSection));
        OnPropertyChanged(nameof(IsAppearanceSection));
    }

    partial void OnGamePathChanged(string value)
    {
        UpdateGamePathStatus();
        PersistGamePath();
    }

    protected override void OnLocalizationChanged()
    {
        UpdateGamePathStatus();
        if (!string.IsNullOrWhiteSpace(_currentRole))
            RoleLabel = MapRoleLabel(_currentRole);
        RefreshProfiles();
    }

    [SupportedOSPlatform("windows")]
    private static SteamDetectionResult DetectOnWindows() => SteamLocator.DetectUnturned();
}

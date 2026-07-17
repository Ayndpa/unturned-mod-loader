using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Threading;
using UnturnedModLoader.Services.Api;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnturnedModLoader.I18n;
using UnturnedModLoader.Models;
using UnturnedModLoader.Services;
using UnturnedModLoader.Services.WinFsp;
using UnturnedModLoader.Views;

namespace UnturnedModLoader.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly SettingsService _settingsService;
    private readonly AppSettings _settings;
    private readonly FolderPickerService _folderPicker;
    private readonly AuthSessionService _session;
    private readonly ProfileService _profileService;
    private readonly VirtualFilesystemService _vfs;
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

    [ObservableProperty]
    private string _winFspStatusText = "";

    [ObservableProperty]
    private bool _isWinFspInstalled;

    [ObservableProperty]
    private bool _isWinFspChecking;

    [ObservableProperty]
    private bool _canInstallWinFsp;

    [ObservableProperty]
    private bool _isWinFspInstalling;

    [ObservableProperty]
    private string _winFspActionStatus = "";

    [ObservableProperty]
    private string _mountStatusText = "";

    [ObservableProperty]
    private string _updateStatusText = "";

    [ObservableProperty]
    private bool _isCheckingForUpdates;

    [ObservableProperty]
    private string _currentVersionText = "";

    /// <summary>Download-mirror picker shared with the onboarding wizard.</summary>
    public WinFspMirrorPickerViewModel MirrorPicker { get; } = new();

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
    public bool IsAboutSection => SelectedSection == "about";

    public ObservableCollection<ProfileItemViewModel> Profiles { get; } = [];

    public event Action? CloseRequested;
    public event Action? OnboardingResetRequested;

    public SettingsViewModel(
        SettingsService settingsService,
        AppSettings settings,
        FolderPickerService folderPicker,
        AuthSessionService session,
        ProfileService profileService,
        VirtualFilesystemService vfs,
        Window owner,
        string? initialSection = null)
    {
        _settingsService = settingsService;
        _settings = settings;
        _folderPicker = folderPicker;
        _session = session;
        _profileService = profileService;
        _vfs = vfs;
        _owner = owner;

        _gamePath = settings.GamePath;
        _username = settings.Username ?? "";
        _selectedLocale = string.IsNullOrWhiteSpace(settings.Locale)
            ? LocalizationService.DetectDefaultLocaleCode()
            : settings.Locale;
        _selectedTheme = ThemeService.NormalizePreference(settings.Theme);
        _newProfileName = L.Get(ProfileKeys.NewName);
        _currentVersionText = L.Get(I18n.Settings.AppVersionDesc, typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");

        if (!string.IsNullOrWhiteSpace(initialSection))
            _selectedSection = initialSection;

        UpdateGamePathStatus();
        RefreshProfiles();
        RefreshWinFspStatus();

        MirrorPicker.RefreshLabels();
        MirrorPicker.TestCommand.Execute(null);

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
    private async Task ResetOnboardingAsync()
    {
        var confirmed = await DialogService.ConfirmAsync(
            _owner,
            L.Get(I18n.Settings.ResetOnboardingConfirmTitle),
            L.Get(I18n.Settings.ResetOnboardingConfirmMsg),
            confirmText: L.Get(Common.Confirm),
            cancelText: L.Get(Common.Cancel)
        );

        if (!confirmed)
            return;

        _settings.OnboardingCompleted = false;
        _settingsService.Save(_settings);
        OnboardingResetRequested?.Invoke();
    }

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
    private async Task RefreshWinFspStatusAsync()
    {
        if (IsWinFspChecking || IsWinFspInstalling)
            return;

        IsWinFspChecking = true;
        WinFspActionStatus = L.Get(WinFspKeys.Checking);

        try
        {
            await Task.Run(RefreshWinFspStatus);
        }
        finally
        {
            IsWinFspChecking = false;
        }
    }

    [RelayCommand]
    private async Task InstallWinFspAsync()
    {
        if (IsWinFspInstalling)
            return;

        WinFspActionStatus = "";

        if (!OperatingSystem.IsWindows())
        {
            WinFspActionStatus = L.Get(WinFspKeys.NotApplicable);
            return;
        }

        IsWinFspInstalling = true;
        CanInstallWinFsp = false;
        WinFspActionStatus = L.Get(WinFspKeys.InstallStarted);

        try
        {
            var progress = new Progress<WinFspInstallProgress>(p =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    WinFspActionStatus = p.Phase switch
                    {
                        WinFspInstallPhase.Downloading => string.IsNullOrWhiteSpace(p.Message)
                            ? L.Get(WinFspKeys.Downloading)
                            : p.Message,
                        WinFspInstallPhase.Uninstalling => L.Get(WinFspKeys.Uninstalling),
                        WinFspInstallPhase.Installing => L.Get(WinFspKeys.Installing),
                        WinFspInstallPhase.Remounting => L.Get(WinFspKeys.Remounting),
                        _ => string.IsNullOrWhiteSpace(p.Message) ? WinFspActionStatus : p.Message,
                    };
                });
            });

            var result = await WinFspService.InstallOrUpgradeAsync(
                MirrorPicker.SelectedMirror,
                progress);

            if (!result.Success)
            {
                WinFspActionStatus = string.Equals(result.Message, "UAC_CANCELLED", StringComparison.Ordinal)
                    ? L.Get(WinFspKeys.UacCancelled)
                    : L.Get(WinFspKeys.InstallFailed, result.Message);
                ApplyWinFspStatus(result.Status);
                return;
            }

            ApplyWinFspStatus(result.Status);
            WinFspActionStatus = L.Get(WinFspKeys.InstallSucceeded);

            WinFspActionStatus = L.Get(WinFspKeys.Remounting);
            var mount = App.CurrentApp?.TryMountVfs() ?? _vfs.Mount();
            if (mount.Success)
            {
                WinFspActionStatus = L.Get(
                    WinFspKeys.RemountSucceeded,
                    _vfs.MountPoint ?? "");
            }
            else
            {
                WinFspActionStatus = L.Get(
                    WinFspKeys.RemountFailed,
                    mount.Error ?? "unknown");
            }

            RefreshMountStatus();
        }
        catch (Exception ex)
        {
            WinFspActionStatus = L.Get(WinFspKeys.InstallFailed, ex.GetBaseException().Message);
            RefreshWinFspStatus();
        }
        finally
        {
            IsWinFspInstalling = false;
            if (!IsWinFspInstalled)
                CanInstallWinFsp = true;
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

        if (GameProcessService.IsRunning(_settings.GamePath, _vfs.DriveLetter))
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
        if (profile is null)
            return;

        if (GameProcessService.IsRunning(_settings.GamePath, _vfs.DriveLetter))
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
        if (profile is null)
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
    private void OpenRegister()
    {
        var baseUrl = ModsApiClientFactory.ResolveBaseUrl(_settings).TrimEnd('/');
        var url = $"{baseUrl}/register";
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
            !GameProcessService.IsRunning(GamePath, _vfs.DriveLetter))
        {
            // Repoint the volume's lower layer at the new game install (no remount).
            _vfs.SetLowerRoot(GamePath);
            _profileService.SyncActiveMounts();
        }
    }

    private void RefreshWinFspStatus()
    {
        if (!IsWinFspInstalling)
            WinFspActionStatus = "";

        if (!OperatingSystem.IsWindows())
        {
            IsWinFspInstalled = false;
            CanInstallWinFsp = false;
            WinFspStatusText = L.Get(WinFspKeys.NotApplicable);
            RefreshMountStatus();
            return;
        }

        ApplyWinFspStatus(WinFspService.GetStatus());
    }

    private void ApplyWinFspStatus(WinFspStatus status)
    {
        switch (status.State)
        {
            case WinFspInstallState.Installed:
                IsWinFspInstalled = true;
                CanInstallWinFsp = false;
                var suffix = string.IsNullOrWhiteSpace(status.Version)
                    ? ""
                    : $" ({status.Version})";
                WinFspStatusText = L.Get(WinFspKeys.Installed, suffix);
                break;
            case WinFspInstallState.WrongVersion:
                IsWinFspInstalled = false;
                CanInstallWinFsp = !IsWinFspInstalling;
                WinFspStatusText = L.Get(
                    WinFspKeys.WrongVersion,
                    status.Version ?? "?",
                    WinFspMirrorService.RequiredNativeVersion);
                break;
            case WinFspInstallState.NotInstalled:
                IsWinFspInstalled = false;
                CanInstallWinFsp = !IsWinFspInstalling;
                WinFspStatusText = L.Get(WinFspKeys.NotInstalled);
                break;
            default:
                IsWinFspInstalled = false;
                CanInstallWinFsp = false;
                WinFspStatusText = L.Get(WinFspKeys.NotApplicable);
                break;
        }

        RefreshMountStatus();
    }

    /// <summary>Reflects the live WinFsp virtual-drive mount state into the UI.</summary>
    private void RefreshMountStatus()
    {
        MountStatusText = _vfs.IsMounted
            ? L.Get(Main.MountedAt, _vfs.MountPoint ?? "")
            : L.Get(Main.NotMounted);
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
        OnPropertyChanged(nameof(IsAboutSection));
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        if (IsCheckingForUpdates) return;

        IsCheckingForUpdates = true;
        UpdateStatusText = L.Get(I18n.Settings.CheckingUpdates);

        try
        {
            var release = await UpdateService.CheckForUpdatesAsync();
            if (release == null)
            {
                UpdateStatusText = L.Get(I18n.Settings.CheckUpdatesFailed);
                return;
            }

            if (UpdateService.IsNewerVersion(release.TagName, out var latestVersion))
            {
                UpdateStatusText = L.Get(I18n.Settings.NewVersionAvailable, release.TagName);

                var title = L.Get(I18n.Settings.UpdateDialogTitle);
                var message = L.Get(I18n.Settings.UpdateDialogMessage, release.TagName, release.Body);
                var confirm = await DialogService.ConfirmAsync(
                    _owner,
                    title,
                    message,
                    confirmText: L.Get(I18n.Settings.GoToDownload),
                    cancelText: L.Get(I18n.Common.Cancel),
                    useMarkdown: true
                );

                if (confirm)
                {
                    var url = release.HtmlUrl;
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        var psi = new ProcessStartInfo
                        {
                            FileName = url,
                            UseShellExecute = true
                        };
                        Process.Start(psi);
                    }
                }
            }
            else
            {
                UpdateStatusText = L.Get(I18n.Settings.IsLatestVersion);
            }
        }
        catch (Exception ex)
        {
            UpdateStatusText = L.Get(I18n.Settings.CheckUpdatesFailed) + ": " + ex.Message;
        }
        finally
        {
            IsCheckingForUpdates = false;
        }
    }

    partial void OnGamePathChanged(string value)
    {
        UpdateGamePathStatus();
        PersistGamePath();
    }

    protected override void OnLocalizationChanged()
    {
        UpdateGamePathStatus();
        RefreshWinFspStatus();
        RefreshMountStatus();
        MirrorPicker.RefreshLabels();
        if (!string.IsNullOrWhiteSpace(_currentRole))
            RoleLabel = MapRoleLabel(_currentRole);
        RefreshProfiles();
        CurrentVersionText = L.Get(I18n.Settings.AppVersionDesc, typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");
    }

    [SupportedOSPlatform("windows")]
    private static SteamDetectionResult DetectOnWindows() => SteamLocator.DetectUnturned();
}

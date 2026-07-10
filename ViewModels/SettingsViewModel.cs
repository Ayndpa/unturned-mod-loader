using System.Runtime.Versioning;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnturnedModLoader.I18n;
using UnturnedModLoader.Models;
using UnturnedModLoader.Models.Api;
using UnturnedModLoader.Services;
using UnturnedModLoader.Services.Api;

namespace UnturnedModLoader.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly SettingsService _settingsService;
    private readonly AppSettings _settings;
    private readonly FolderPickerService _folderPicker;
    private readonly AuthSessionService _session;
    private readonly IModsApiClient _modsApi;
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
    private string _selectedApiProvider = "Local";

    [ObservableProperty]
    private string _localApiBaseUrl = "";

    [ObservableProperty]
    private string _cloudApiBaseUrl = "";

    [ObservableProperty]
    private string _apiStatus = "";

    [ObservableProperty]
    private bool _isSavingApi;

    [ObservableProperty]
    private string _selectedLocale = "zh";

    public string CurrentApiEndpoint => _modsApi.BaseUrl;
    public IReadOnlyList<string> ApiProviderOptions { get; } = ["Local", "Cloud"];
    public IReadOnlyList<string> LocaleOptions { get; } = ["zh", "en"];
    public bool IsZhLocaleSelected => SelectedLocale == "zh";
    public bool IsEnLocaleSelected => SelectedLocale == "en";
    public bool IsGameSection => SelectedSection == "game";
    public bool IsAccountSection => SelectedSection == "account";
    public bool IsApiSection => SelectedSection == "api";
    public bool IsLanguageSection => SelectedSection == "language";

    public event Action? CloseRequested;
    public event Action? LogoutRequested;

    public SettingsViewModel(
        SettingsService settingsService,
        AppSettings settings,
        FolderPickerService folderPicker,
        AuthSessionService session,
        IModsApiClient modsApi)
    {
        _settingsService = settingsService;
        _settings = settings;
        _folderPicker = folderPicker;
        _session = session;
        _modsApi = modsApi;

        _gamePath = settings.GamePath;
        _localApiBaseUrl = settings.LocalApiBaseUrl;
        _cloudApiBaseUrl = settings.CloudApiBaseUrl;
        _selectedApiProvider = settings.ApiProvider.ToString();
        _username = settings.Username ?? "";
        _selectedLocale = string.IsNullOrWhiteSpace(settings.Locale)
            ? LocalizationService.DetectDefaultLocaleCode()
            : settings.Locale;

        UpdateGamePathStatus();
        _ = LoadAccountAsync();
    }

    public string GetLocaleLabel(string localeCode) => localeCode switch
    {
        "zh" => L.Get(LocaleKeys.Zh),
        "en" => L.Get(LocaleKeys.En),
        _ => localeCode,
    };

    public string GetApiProviderLabel(string provider) => provider switch
    {
        "Local" => L.Get(ApiProviderLabels.Local),
        "Cloud" => L.Get(ApiProviderLabels.Cloud),
        _ => provider,
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
    private async Task RefreshAccountAsync() => await LoadAccountAsync();

    [RelayCommand]
    private async Task LogoutAsync()
    {
        IsAccountLoading = true;
        AccountStatus = L.Get(Settings.LoggingOut);

        try
        {
            await _session.LogoutAsync();
            LogoutRequested?.Invoke();
        }
        finally
        {
            IsAccountLoading = false;
        }
    }

    [RelayCommand]
    private async Task SaveApiSettingsAsync()
    {
        if (!Enum.TryParse<Models.ApiProvider>(SelectedApiProvider, ignoreCase: true, out var provider))
        {
            ApiStatus = L.Get(Settings.ApiProviderInvalid);
            return;
        }

        if (string.IsNullOrWhiteSpace(LocalApiBaseUrl))
        {
            ApiStatus = L.Get(Settings.LocalApiRequired);
            return;
        }

        if (provider == Models.ApiProvider.Cloud && string.IsNullOrWhiteSpace(CloudApiBaseUrl))
        {
            ApiStatus = L.Get(Settings.CloudApiRequired);
            return;
        }

        IsSavingApi = true;
        ApiStatus = "";

        try
        {
            _settings.ApiProvider = provider;
            _settings.LocalApiBaseUrl = LocalApiBaseUrl.Trim();
            _settings.CloudApiBaseUrl = CloudApiBaseUrl.Trim();
            _settingsService.Save(_settings);
            ApiStatus = L.Get(Settings.ApiSaved);
            await Task.CompletedTask;
        }
        finally
        {
            IsSavingApi = false;
        }
    }

    private async Task LoadAccountAsync()
    {
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

    private void PersistGamePath()
    {
        if (!IsPathValid && !string.IsNullOrWhiteSpace(GamePath))
            return;

        _settings.GamePath = GamePath;
        _settingsService.Save(_settings);
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

    partial void OnSelectedSectionChanged(string value)
    {
        OnPropertyChanged(nameof(IsGameSection));
        OnPropertyChanged(nameof(IsAccountSection));
        OnPropertyChanged(nameof(IsApiSection));
        OnPropertyChanged(nameof(IsLanguageSection));
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
    }

    [SupportedOSPlatform("windows")]
    private static SteamDetectionResult DetectOnWindows() => SteamLocator.DetectUnturned();
}
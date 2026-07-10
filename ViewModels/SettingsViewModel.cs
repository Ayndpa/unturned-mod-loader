using System.Runtime.Versioning;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    public string CurrentApiEndpoint => _modsApi.BaseUrl;
    public IReadOnlyList<string> ApiProviderOptions { get; } = ["Local", "Cloud"];
    public bool IsGameSection => SelectedSection == "game";
    public bool IsAccountSection => SelectedSection == "account";
    public bool IsApiSection => SelectedSection == "api";

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

        UpdateGamePathStatus();
        _ = LoadAccountAsync();
    }

    [RelayCommand]
    private void SelectSection(string section)
    {
        if (string.IsNullOrWhiteSpace(section))
            return;

        SelectedSection = section;
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();

    [RelayCommand]
    private async Task AutoDetectGamePathAsync()
    {
        if (IsDetecting)
            return;

        IsDetecting = true;
        GamePathStatus = "正在从 Steam 注册表与库文件夹中检测…";

        try
        {
            SteamDetectionResult result;
            if (OperatingSystem.IsWindows())
                result = await Task.Run(DetectOnWindows);
            else
                result = new(false, null, "自动检测仅支持 Windows 系统。");

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
            "选择 Unturned 游戏目录",
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
        AccountStatus = "正在退出登录…";

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
        if (!Enum.TryParse<ApiProvider>(SelectedApiProvider, ignoreCase: true, out var provider))
        {
            ApiStatus = "API 来源无效。";
            return;
        }

        if (string.IsNullOrWhiteSpace(LocalApiBaseUrl))
        {
            ApiStatus = "请填写本地 API 地址。";
            return;
        }

        if (provider == ApiProvider.Cloud && string.IsNullOrWhiteSpace(CloudApiBaseUrl))
        {
            ApiStatus = "使用云端 API 时需填写云端地址。";
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
            ApiStatus = "已保存。重新启动应用后 API 连接设置才会生效。";
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
                AccountStatus = "无法获取账号信息，请尝试重新登录。";
                Username = _settings.Username ?? "";
                Email = "";
                RoleLabel = "";
                return;
            }

            Username = result.Username;
            Email = result.Email;
            RoleLabel = MapRoleLabel(result.Role);
            AccountStatus = "账号信息已同步。";
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
            ? "游戏目录有效。"
            : string.IsNullOrWhiteSpace(GamePath)
                ? "尚未配置游戏目录。"
                : $"无效目录：未找到 {GamePathValidator.ExecutableName}";
    }

    private static string MapRoleLabel(string role) => role switch
    {
        "admin" => "管理员",
        "moderator" => "版主",
        _ => "用户",
    };

    partial void OnSelectedSectionChanged(string value)
    {
        OnPropertyChanged(nameof(IsGameSection));
        OnPropertyChanged(nameof(IsAccountSection));
        OnPropertyChanged(nameof(IsApiSection));
    }

    partial void OnGamePathChanged(string value)
    {
        UpdateGamePathStatus();
        PersistGamePath();
    }

    [SupportedOSPlatform("windows")]
    private static SteamDetectionResult DetectOnWindows() => SteamLocator.DetectUnturned();
}
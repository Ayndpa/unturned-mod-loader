using System.Runtime.Versioning;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnturnedModLoader.I18n;
using UnturnedModLoader.Models;
using UnturnedModLoader.Services;

namespace UnturnedModLoader.ViewModels;

public partial class OnboardingViewModel : ViewModelBase
{
    private readonly SettingsService _settingsService;
    private readonly AppSettings _settings;
    private readonly FolderPickerService _folderPicker;
    private readonly ProfileService _profileService;

    /// <summary>
    /// Whether a WinFsp step should be shown (Windows only, not already installed).
    /// Evaluated once at construction so the step count stays stable during the wizard.
    /// </summary>
    private readonly bool _showWinFspStep;

    [ObservableProperty]
    private int _currentStep = 1;

    [ObservableProperty]
    private string _gamePath = "";

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private bool _isDetecting;

    // ── WinFsp step state ────────────────────────────────────────────────────

    [ObservableProperty]
    private string _winFspStatusText = "";

    [ObservableProperty]
    private bool _isWinFspInstalled;

    [ObservableProperty]
    private bool _isWinFspChecking;

    [ObservableProperty]
    private bool _canInstallWinFsp;

    [ObservableProperty]
    private string _winFspActionStatus = "";

    /// <summary>Download-mirror picker shared with the settings window.</summary>
    public WinFspMirrorPickerViewModel MirrorPicker { get; } = new();

    // ── Computed properties ──────────────────────────────────────────────────

    public int TotalSteps => _showWinFspStep ? 3 : 2;

    public bool IsWelcomeStep  => CurrentStep == 1;
    public bool IsGamePathStep => CurrentStep == 2;
    public bool IsWinFspStep   => CurrentStep == 3 && _showWinFspStep;

    /// <summary>Show "Next" when there are more steps ahead.</summary>
    public bool IsNextVisible => CurrentStep < TotalSteps;

    /// <summary>Show "Finish" on the last step.</summary>
    public bool IsFinishVisible => CurrentStep == TotalSteps;

    /// <summary>Show "Back" when past the first step.</summary>
    public bool IsBackVisible => CurrentStep > 1;

    public string StepIndicator => L.Get(Onboarding.StepIndicator, CurrentStep, TotalSteps);
    public bool IsPathValid => GamePathValidator.IsValid(GamePath);

    public event Action<AppSettings>? Completed;

    public OnboardingViewModel(
        SettingsService settingsService,
        AppSettings settings,
        FolderPickerService folderPicker,
        ProfileService profileService)
    {
        _settingsService = settingsService;
        _settings = settings;
        _folderPicker = folderPicker;
        _profileService = profileService;
        _gamePath = settings.GamePath;

        // Show the WinFsp step on Windows regardless of install state,
        // so users always see its status and can install/verify it during setup.
        _showWinFspStep = OperatingSystem.IsWindows();

        MirrorPicker.RefreshLabels();
        UpdateStatus();
    }

    // ── Navigation commands ──────────────────────────────────────────────────

    [RelayCommand]
    private void Next()
    {
        if (CurrentStep < TotalSteps)
            CurrentStep++;
    }

    [RelayCommand]
    private void Back()
    {
        if (CurrentStep > 1)
            CurrentStep--;
    }

    [RelayCommand]
    private void Finish() => CompleteOnboarding(saveGamePath: true);

    [RelayCommand]
    private void Skip() => CompleteOnboarding(saveGamePath: false);

    // ── Game-path step ───────────────────────────────────────────────────────

    [RelayCommand]
    private async Task AutoDetectAsync()
    {
        if (IsDetecting)
            return;

        IsDetecting = true;
        StatusMessage = L.Get(GamePathKeys.Detecting);

        try
        {
            SteamDetectionResult result;
            if (OperatingSystem.IsWindows())
                result = await Task.Run(DetectOnWindows);
            else
                result = new(false, null, L.Get(Steam.WindowsOnly));

            if (result.Success && result.GamePath is not null)
                GamePath = result.GamePath;

            StatusMessage = result.Message;
        }
        finally
        {
            IsDetecting = false;
        }
    }

    [RelayCommand]
    private async Task BrowseAsync()
    {
        var picked = await _folderPicker.PickFolderAsync(
            L.Get(GamePathKeys.PickerTitle),
            string.IsNullOrWhiteSpace(GamePath) ? null : GamePath);

        if (picked is not null)
            GamePath = picked;
    }

    // ── WinFsp step ──────────────────────────────────────────────────────────

    [RelayCommand]
    [SupportedOSPlatform("windows")]
    private async Task RefreshWinFspStatusAsync()
    {
        if (IsWinFspChecking)
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
    [SupportedOSPlatform("windows")]
    private void InstallWinFsp()
    {
        WinFspActionStatus = "";

        if (!OperatingSystem.IsWindows())
        {
            WinFspActionStatus = L.Get(WinFspKeys.NotApplicable);
            return;
        }

        if (!WinFspService.IsScriptBundlePresent())
        {
            WinFspActionStatus = L.Get(WinFspKeys.BundleMissing);
            return;
        }

        var (started, message) = WinFspService.StartElevatedInstall(MirrorPicker.SelectedMirror);
        if (!started)
        {
            WinFspActionStatus = string.IsNullOrWhiteSpace(message)
                ? L.Get(WinFspKeys.UacCancelled)
                : L.Get(WinFspKeys.InstallFailed, message);
            return;
        }

        WinFspActionStatus = L.Get(WinFspKeys.InstallStarted);
    }

    [SupportedOSPlatform("windows")]
    private void RefreshWinFspStatus()
    {
        WinFspActionStatus = "";

        if (!OperatingSystem.IsWindows())
        {
            IsWinFspInstalled = false;
            CanInstallWinFsp  = false;
            WinFspStatusText  = L.Get(WinFspKeys.NotApplicable);
            return;
        }

        var status = WinFspService.IsScriptBundlePresent()
            ? WinFspService.RefreshFromScript()
            : WinFspService.GetStatus();

        switch (status.State)
        {
            case WinFspInstallState.Installed:
                IsWinFspInstalled = true;
                CanInstallWinFsp  = false;
                var suffix = status.Version is { } v ? $" ({v})" : "";
                WinFspStatusText  = L.Get(WinFspKeys.Installed, suffix);
                break;

            case WinFspInstallState.NotInstalled:
                IsWinFspInstalled = false;
                CanInstallWinFsp  = WinFspService.IsScriptBundlePresent();
                WinFspStatusText  = L.Get(WinFspKeys.NotInstalled);
                break;

            default:
                IsWinFspInstalled = false;
                CanInstallWinFsp  = false;
                WinFspStatusText  = L.Get(WinFspKeys.NotApplicable);
                break;
        }
    }

    // ── Completion ───────────────────────────────────────────────────────────

    private void CompleteOnboarding(bool saveGamePath)
    {
        _settings.OnboardingCompleted = true;
        _settings.SettingsVersion = 2;
        _settings.GamePath = saveGamePath && IsPathValid ? GamePath : "";

        _profileService.EnsureAtLeastOneProfile();

        if (saveGamePath && IsPathValid)
        {
            var profiles = _profileService.List();
            if (string.IsNullOrWhiteSpace(_settings.ActiveProfileId)
                || _profileService.GetById(_settings.ActiveProfileId) is null)
            {
                _settings.ActiveProfileId = profiles[0].Id;
            }

            _settingsService.Save(_settings);
            _profileService.SetActive(_settings.ActiveProfileId);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(_settings.ActiveProfileId))
                _settings.ActiveProfileId = _profileService.List()[0].Id;
            _settingsService.Save(_settings);
        }

        Completed?.Invoke(_settings);
    }

    // ── Change handlers ──────────────────────────────────────────────────────

    partial void OnCurrentStepChanged(int value)
    {
        OnPropertyChanged(nameof(IsWelcomeStep));
        OnPropertyChanged(nameof(IsGamePathStep));
        OnPropertyChanged(nameof(IsWinFspStep));
        OnPropertyChanged(nameof(IsNextVisible));
        OnPropertyChanged(nameof(IsFinishVisible));
        OnPropertyChanged(nameof(IsBackVisible));
        OnPropertyChanged(nameof(StepIndicator));

        // Auto-check WinFsp status when the user enters that step.
        if (IsWinFspStep && OperatingSystem.IsWindows())
        {
            _ = RefreshWinFspStatusAsync();
            MirrorPicker.TestCommand.Execute(null);
        }
    }

    partial void OnGamePathChanged(string value) => UpdateStatus();

    protected override void OnLocalizationChanged()
    {
        MirrorPicker.RefreshLabels();
        // Re-evaluate WinFsp status text in the new locale.
        if (OperatingSystem.IsWindows())
            _ = RefreshWinFspStatusAsync();
    }

    private void UpdateStatus()
    {
        StatusMessage = IsPathValid
            ? L.Get(GamePathKeys.Valid)
            : string.IsNullOrWhiteSpace(GamePath)
                ? L.Get(GamePathKeys.NotConfigured)
                : L.Get(GamePathKeys.Invalid, GamePathValidator.ExecutableName);
    }

    [SupportedOSPlatform("windows")]
    private static SteamDetectionResult DetectOnWindows() => SteamLocator.DetectUnturned();
}
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

    [ObservableProperty]
    private int _currentStep = 1;

    [ObservableProperty]
    private string _gamePath = "";

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private bool _isDetecting;

    public bool IsWelcomeStep => CurrentStep == 1;
    public bool IsGamePathStep => CurrentStep == 2;
    public string StepIndicator => L.Get(Onboarding.StepIndicator, CurrentStep);
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
        UpdateStatus();
    }

    [RelayCommand]
    private void Next()
    {
        if (CurrentStep < 2)
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

    private bool CanFinish => IsPathValid;

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

    partial void OnCurrentStepChanged(int value)
    {
        OnPropertyChanged(nameof(IsWelcomeStep));
        OnPropertyChanged(nameof(IsGamePathStep));
        OnPropertyChanged(nameof(StepIndicator));
    }

    partial void OnGamePathChanged(string value) => UpdateStatus();

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
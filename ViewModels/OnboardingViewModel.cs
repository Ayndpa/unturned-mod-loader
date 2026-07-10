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

    [ObservableProperty]
    private int _currentStep;

    [ObservableProperty]
    private string _gamePath = "";

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private bool _isDetecting;

    [ObservableProperty]
    private bool _isPathValid;

    public bool IsWelcomeStep => CurrentStep == 0;
    public bool IsGamePathStep => CurrentStep == 1;
    public string StepIndicator => L.Get(Onboarding.StepIndicator, CurrentStep + 1);

    public event Action<AppSettings>? Completed;

    public OnboardingViewModel(
        SettingsService settingsService,
        AppSettings settings,
        FolderPickerService folderPicker)
    {
        _settingsService = settingsService;
        _settings = settings;
        _folderPicker = folderPicker;
        _gamePath = settings.GamePath;
        UpdatePathValidity();
    }

    [RelayCommand]
    private void Next()
    {
        if (CurrentStep >= 1)
            return;

        CurrentStep = 1;
    }

    [RelayCommand]
    private void Back()
    {
        if (CurrentStep <= 0)
            return;

        CurrentStep = 0;
    }

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

        if (picked is null)
            return;

        GamePath = picked;
        StatusMessage = IsPathValid
            ? L.Get(GamePathKeys.SelectedValid)
            : L.Get(GamePathKeys.SelectedInvalid, GamePathValidator.ExecutableName);
    }

    [RelayCommand(CanExecute = nameof(CanFinish))]
    private void Finish() => CompleteOnboarding(saveGamePath: true);

    [RelayCommand]
    private void Skip() => CompleteOnboarding(saveGamePath: false);

    private bool CanFinish => IsPathValid;

    private void CompleteOnboarding(bool saveGamePath)
    {
        _settings.OnboardingCompleted = true;
        _settings.GamePath = saveGamePath && IsPathValid ? GamePath : "";
        _settingsService.Save(_settings);
        Completed?.Invoke(_settings);
    }

    partial void OnCurrentStepChanged(int value)
    {
        OnPropertyChanged(nameof(IsWelcomeStep));
        OnPropertyChanged(nameof(IsGamePathStep));
        OnPropertyChanged(nameof(StepIndicator));

        if (value == 1)
            _ = AutoDetectAsync();
    }

    partial void OnGamePathChanged(string value)
    {
        UpdatePathValidity();
        FinishCommand.NotifyCanExecuteChanged();

        if (IsPathValid)
            StatusMessage = L.Get(GamePathKeys.SelectedValid);
    }

    partial void OnIsPathValidChanged(bool value) =>
        FinishCommand.NotifyCanExecuteChanged();

    private void UpdatePathValidity()
    {
        IsPathValid = GamePathValidator.IsValid(GamePath);
    }

    protected override void OnLocalizationChanged() =>
        OnPropertyChanged(nameof(StepIndicator));

    [SupportedOSPlatform("windows")]
    private static SteamDetectionResult DetectOnWindows() => SteamLocator.DetectUnturned();
}
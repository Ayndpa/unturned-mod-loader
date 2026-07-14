using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using UnturnedModLoader.Models;
using UnturnedModLoader.Services;
using UnturnedModLoader.Services.Api;
using UnturnedModLoader.ViewModels;
using UnturnedModLoader.Views;

namespace UnturnedModLoader;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settingsService = new SettingsService();
            var settings = settingsService.Load();

            if (string.IsNullOrWhiteSpace(settings.Locale))
                settings.Locale = LocalizationService.DetectDefaultLocaleCode();

            LocalizationService.Initialize(settings);
            ThemeService.Initialize(settings);

            var overlayService = new GameOverlayService();
            settingsService.MigrateIfNeeded(settings, overlayService);

            var profileService = new ProfileService(settingsService, settings, overlayService);
            profileService.SyncActiveMounts();

            if (!settings.OnboardingCompleted)
                ShowOnboarding(desktop, settingsService, settings, profileService, overlayService);
            else
                ShowMain(desktop, settingsService, settings, profileService, overlayService);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void ShowOnboarding(
        IClassicDesktopStyleApplicationLifetime desktop,
        SettingsService settingsService,
        AppSettings settings,
        ProfileService profileService,
        GameOverlayService overlayService)
    {
        var onboardingWindow = new OnboardingWindow();
        var folderPicker = new FolderPickerService(onboardingWindow);
        var viewModel = new OnboardingViewModel(settingsService, settings, folderPicker, profileService);

        viewModel.Completed += completedSettings =>
        {
            profileService.SyncActiveMounts();
            ShowMain(desktop, settingsService, completedSettings, profileService, overlayService);
            onboardingWindow.Close();
        };

        onboardingWindow.DataContext = viewModel;
        desktop.MainWindow = onboardingWindow;
    }

    private static void ShowMain(
        IClassicDesktopStyleApplicationLifetime desktop,
        SettingsService settingsService,
        AppSettings settings,
        ProfileService profileService,
        GameOverlayService overlayService)
    {
        var api = new ApiClientBundle(settings);
        var mainWindow = CreateMainWindow(settingsService, settings, api, profileService, overlayService);
        desktop.MainWindow = mainWindow;
        mainWindow.Show();
    }

    private static MainWindow CreateMainWindow(
        SettingsService settingsService,
        AppSettings settings,
        ApiClientBundle api,
        ProfileService profileService,
        GameOverlayService overlayService)
    {
        var mainWindow = new MainWindow();
        var folderPicker = new FolderPickerService(mainWindow);
        var session = new AuthSessionService(api, settingsService, settings);

        var imageService = new RemoteImageService(api.SharedHttpClient);
        var installedModsService = new InstalledModsService();
        var active = profileService.GetActive();
        installedModsService.UseProfile(active.Id, active.IsVanilla);
        var sessionCapture = new GameSessionCaptureService(overlayService);

        mainWindow.DataContext = new MainViewModel(
            settingsService,
            settings,
            folderPicker,
            api.Mods,
            session,
            imageService,
            installedModsService,
            profileService,
            overlayService,
            sessionCapture,
            mainWindow);

        return mainWindow;
    }
}

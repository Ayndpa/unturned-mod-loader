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
            var profileService = new ProfileService(settingsService, settings, overlayService);
            settingsService.MigrateIfNeeded(settings, overlayService, profileService);
            profileService.EnsureAtLeastOneProfile();
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

        // Register the unmod:// scheme so future browser installs can launch us.
        ProtocolRegistrar.EnsureRegistered();

        // If launched from a browser/CLI with an install intent, act on it now.
        if (Program.PendingInstallModId is { } modId && mainWindow.DataContext is MainViewModel vm)
        {
            vm.ConsumePendingInstall(modId);
            Program.PendingInstallModId = null;
        }
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
        var scriptService = new ModScriptService();
        var installedModsService = new InstalledModsService(scriptService);
        var active = profileService.GetActive();
        installedModsService.UseProfile(active.Id);
        var sessionCapture = new GameSessionCaptureService(overlayService);
        var downloadService = new ModDownloadService(scriptService, installedModsService);

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
            downloadService,
            mainWindow);

        return mainWindow;
    }
}

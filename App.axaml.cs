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

            var vfs = new VirtualFilesystemService();
            var profileService = new ProfileService(settingsService, settings, vfs);
            settingsService.MigrateIfNeeded(settings, profileService);
            profileService.EnsureAtLeastOneProfile();
            profileService.SyncActiveMounts();

            // Best-effort removal of junctions the legacy overlay left in the real install.
            LeftoverJunctionCleanup.Run(settings.GamePath);

            // Mount the virtual drive for the whole process lifetime (non-fatal on failure).
            _ = vfs.Mount();

            desktop.ShutdownRequested += (_, _) => vfs.Dispose();

            if (!settings.OnboardingCompleted)
                ShowOnboarding(desktop, settingsService, settings, profileService, vfs);
            else
                ShowMain(desktop, settingsService, settings, profileService, vfs);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void ShowOnboarding(
        IClassicDesktopStyleApplicationLifetime desktop,
        SettingsService settingsService,
        AppSettings settings,
        ProfileService profileService,
        VirtualFilesystemService vfs)
    {
        var onboardingWindow = new OnboardingWindow();
        var folderPicker = new FolderPickerService(onboardingWindow);
        var viewModel = new OnboardingViewModel(settingsService, settings, folderPicker, profileService);

        viewModel.Completed += completedSettings =>
        {
            profileService.SyncActiveMounts();
            ShowMain(desktop, settingsService, completedSettings, profileService, vfs);
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
        VirtualFilesystemService vfs)
    {
        var api = new ApiClientBundle(settings);
        var mainWindow = CreateMainWindow(settingsService, settings, api, profileService, vfs);
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

        // When the user resets the setup wizard from Settings > About, close the main window
        // and re-open the onboarding flow.
        if (mainWindow.DataContext is MainViewModel mainVm)
        {
            mainVm.OnboardingResetRequested += () =>
            {
                mainWindow.Close();
                ShowOnboarding(desktop, settingsService, settings, profileService, vfs);
            };
        }
    }

    private static MainWindow CreateMainWindow(
        SettingsService settingsService,
        AppSettings settings,
        ApiClientBundle api,
        ProfileService profileService,
        VirtualFilesystemService vfs)
    {
        var mainWindow = new MainWindow();
        var folderPicker = new FolderPickerService(mainWindow);
        var session = new AuthSessionService(api, settingsService, settings);

        var imageService = new RemoteImageService(api.SharedHttpClient);
        var installedModsService = new InstalledModsService();
        var active = profileService.GetActive();
        installedModsService.UseProfile(active.Id);
        var downloadService = new ModDownloadService(installedModsService);

        mainWindow.DataContext = new MainViewModel(
            settingsService,
            settings,
            folderPicker,
            api.Mods,
            session,
            imageService,
            installedModsService,
            profileService,
            vfs,
            downloadService,
            mainWindow);

        return mainWindow;
    }
}

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

            if (!settings.OnboardingCompleted)
                ShowOnboarding(desktop, settingsService, settings);
            else
                ShowMain(desktop, settingsService, settings);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void ShowOnboarding(
        IClassicDesktopStyleApplicationLifetime desktop,
        SettingsService settingsService,
        AppSettings settings)
    {
        var onboardingWindow = new OnboardingWindow();
        var folderPicker = new FolderPickerService(onboardingWindow);
        var viewModel = new OnboardingViewModel(settingsService, settings, folderPicker);

        viewModel.Completed += completedSettings =>
        {
            ShowMain(desktop, settingsService, completedSettings);
            onboardingWindow.Close();
        };

        onboardingWindow.DataContext = viewModel;
        desktop.MainWindow = onboardingWindow;
    }

    private static void ShowMain(
        IClassicDesktopStyleApplicationLifetime desktop,
        SettingsService settingsService,
        AppSettings settings)
    {
        var api = new ApiClientBundle(settings);
        var mainWindow = CreateMainWindow(settingsService, settings, api);
        desktop.MainWindow = mainWindow;
        mainWindow.Show();
    }

    private static MainWindow CreateMainWindow(
        SettingsService settingsService,
        AppSettings settings,
        ApiClientBundle api)
    {
        var mainWindow = new MainWindow();
        var folderPicker = new FolderPickerService(mainWindow);
        var session = new AuthSessionService(api, settingsService, settings);

        var imageService = new RemoteImageService(api.SharedHttpClient);
        var installedModsService = new InstalledModsService();

        mainWindow.DataContext = new MainViewModel(
            settingsService,
            settings,
            folderPicker,
            api.Mods,
            session,
            imageService,
            installedModsService,
            mainWindow);

        return mainWindow;
    }
}
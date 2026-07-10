using Avalonia;
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

            if (!settings.OnboardingCompleted)
                ShowOnboarding(desktop, settingsService, settings);
            else
                ShowMainWindow(desktop, settingsService, settings);
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
            var mainWindow = CreateMainWindow(settingsService, completedSettings);
            desktop.MainWindow = mainWindow;
            mainWindow.Show();
            onboardingWindow.Close();
        };

        onboardingWindow.DataContext = viewModel;
        desktop.MainWindow = onboardingWindow;
    }

    private static void ShowMainWindow(
        IClassicDesktopStyleApplicationLifetime desktop,
        SettingsService settingsService,
        AppSettings settings)
    {
        var mainWindow = CreateMainWindow(settingsService, settings);
        desktop.MainWindow = mainWindow;
    }

    private static MainWindow CreateMainWindow(SettingsService settingsService, AppSettings settings)
    {
        var mainWindow = new MainWindow();
        var folderPicker = new FolderPickerService(mainWindow);
        var modsApi = ModsApiClientFactory.Create(settings);
        mainWindow.DataContext = new MainViewModel(settingsService, settings, folderPicker, modsApi);
        return mainWindow;
    }
}
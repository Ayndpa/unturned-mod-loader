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

            if (!settings.OnboardingCompleted)
                ShowOnboarding(desktop, settingsService, settings);
            else
                ShowLogin(desktop, settingsService, settings);
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
            ShowLogin(desktop, settingsService, completedSettings);
            onboardingWindow.Close();
        };

        onboardingWindow.DataContext = viewModel;
        desktop.MainWindow = onboardingWindow;
    }

    private static void ShowLogin(
        IClassicDesktopStyleApplicationLifetime desktop,
        SettingsService settingsService,
        AppSettings settings)
    {
        var api = new ApiClientBundle(settings);
        var session = new AuthSessionService(api, settingsService, settings);
        var loginWindow = new LoginWindow();
        var viewModel = new LoginViewModel(session, settings);

        viewModel.LoggedIn += loggedInSettings =>
        {
            var mainWindow = CreateMainWindow(settingsService, loggedInSettings, api);
            desktop.MainWindow = mainWindow;
            mainWindow.Show();
            loginWindow.Close();
        };

        viewModel.RegisterRequested += () =>
        {
            ShowRegister(desktop, settingsService, settings, api, loginWindow);
        };

        loginWindow.DataContext = viewModel;
        desktop.MainWindow = loginWindow;
        loginWindow.Show();
    }

    private static void ShowRegister(
        IClassicDesktopStyleApplicationLifetime desktop,
        SettingsService settingsService,
        AppSettings settings,
        ApiClientBundle api,
        Window loginWindow)
    {
        var session = new AuthSessionService(api, settingsService, settings);
        var registerWindow = new RegisterWindow();
        var viewModel = new RegisterViewModel(session, settings);

        viewModel.Registered += registeredSettings =>
        {
            var mainWindow = CreateMainWindow(settingsService, registeredSettings, api);
            desktop.MainWindow = mainWindow;
            mainWindow.Show();
            registerWindow.Close();
            loginWindow.Close();
        };

        viewModel.LoginRequested += () =>
        {
            registerWindow.Close();
        };

        registerWindow.DataContext = viewModel;
        registerWindow.Show();
    }

    private static MainWindow CreateMainWindow(
        SettingsService settingsService,
        AppSettings settings,
        ApiClientBundle api)
    {
        var mainWindow = new MainWindow();
        var folderPicker = new FolderPickerService(mainWindow);
        mainWindow.DataContext = new MainViewModel(settingsService, settings, folderPicker, api.Mods);
        return mainWindow;
    }
}
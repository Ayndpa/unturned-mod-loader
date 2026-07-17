using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using UnturnedModLoader.Models;
using UnturnedModLoader.Services;
using UnturnedModLoader.Services.Api;
using UnturnedModLoader.ViewModels;
using UnturnedModLoader.Views;

namespace UnturnedModLoader;

public partial class App : Application
{
    private MainViewModel? _mainViewModel;
    private Window? _mainWindow;
    private VirtualFilesystemService? _vfs;
    private ProfileService? _profileService;

    /// <summary>Process-wide VFS host, used after WinFsp install to (re)mount the virtual drive.</summary>
    public static App? CurrentApp => Current as App;

    public VirtualFilesystemService? Vfs => _vfs;

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
            _vfs = vfs;
            var profileService = new ProfileService(settingsService, settings, vfs);
            _profileService = profileService;
            settingsService.MigrateIfNeeded(settings, profileService);
            profileService.EnsureAtLeastOneProfile();
            profileService.SyncActiveMounts();

            // Best-effort removal of junctions the legacy overlay left in the real install.
            LeftoverJunctionCleanup.Run(settings.GamePath);

            // Mount the virtual drive for the whole process lifetime (non-fatal on failure).
            // Mount() itself swallows exceptions; keep the call fire-and-forget safe.
            TryMountVfs();

            desktop.ShutdownRequested += (_, _) => vfs.Dispose();

            if (!settings.OnboardingCompleted)
                ShowOnboarding(desktop, settingsService, settings, profileService, vfs);
            else
                ShowMain(desktop, settingsService, settings, profileService, vfs);

            // Accept activation / install intents from secondary process launches
            // (browser unmod:// clicks while we are already running).
            if (Program.SingleInstance is { } singleInstance)
            {
                singleInstance.Activated += args =>
                    Dispatcher.UIThread.Post(() => HandleSecondaryActivation(args));
                singleInstance.StartListening();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void HandleSecondaryActivation(string[] args)
    {
        // Bring the existing window to the foreground first.
        ActivateMainWindow();

        // If the secondary launch carried an install intent, act on it.
        int? modId = null;
        foreach (var arg in args)
        {
            if (string.Equals(arg, "--install", StringComparison.OrdinalIgnoreCase))
                continue;
            if (ProtocolRegistrar.TryParseInstallIntent(arg, out var id))
            {
                modId = id;
                break;
            }
        }

        if (modId is not { } installId)
            return;

        if (_mainViewModel is { } vm)
        {
            vm.ConsumePendingInstall(installId);
            return;
        }

        // Onboarding still open — park the intent for ShowMain to consume.
        Program.PendingInstallModId = installId;
    }

    private void ActivateMainWindow()
    {
        var window = _mainWindow
            ?? (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (window is null)
            return;

        if (!window.IsVisible)
            window.Show();

        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;

        window.Activate();
        window.Topmost = true;
        window.Topmost = false;
        window.Focus();
    }

    /// <summary>
    /// Attempt to mount the virtual drive and re-sync the active profile overlay.
    /// Safe to call after WinFsp is installed mid-session.
    /// </summary>
    public MountResult TryMountVfs()
    {
        if (_vfs is null)
            return MountResult.Fail("VFS not initialized.");

        MountResult result;
        try
        {
            result = _vfs.Mount();
        }
        catch (Exception ex)
        {
            return MountResult.Fail(ex.GetBaseException().Message);
        }

        try
        {
            _profileService?.SyncActiveMounts();
        }
        catch
        {
            // overlay sync is best-effort
        }

        return result;
    }

    private void ShowOnboarding(
        IClassicDesktopStyleApplicationLifetime desktop,
        SettingsService settingsService,
        AppSettings settings,
        ProfileService profileService,
        VirtualFilesystemService vfs)
    {
        var onboardingWindow = new OnboardingWindow();
        var folderPicker = new FolderPickerService(onboardingWindow);
        var viewModel = new OnboardingViewModel(settingsService, settings, folderPicker, profileService, vfs);

        viewModel.Completed += completedSettings =>
        {
            // WinFsp may have been installed during the wizard — ensure the drive is up.
            TryMountVfs();
            profileService.SyncActiveMounts();
            ShowMain(desktop, settingsService, completedSettings, profileService, vfs);
            onboardingWindow.Close();
        };

        onboardingWindow.DataContext = viewModel;
        desktop.MainWindow = onboardingWindow;
        _mainWindow = onboardingWindow;
    }

    private void ShowMain(
        IClassicDesktopStyleApplicationLifetime desktop,
        SettingsService settingsService,
        AppSettings settings,
        ProfileService profileService,
        VirtualFilesystemService vfs)
    {
        var api = new ApiClientBundle(settings);
        var mainWindow = CreateMainWindow(settingsService, settings, api, profileService, vfs);
        desktop.MainWindow = mainWindow;
        _mainWindow = mainWindow;
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
            _mainViewModel = mainVm;
            mainVm.OnboardingResetRequested += () =>
            {
                _mainViewModel = null;
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

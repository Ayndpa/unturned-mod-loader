using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnturnedModLoader.I18n;
using UnturnedModLoader.Models;
using UnturnedModLoader.Models.Api;
using UnturnedModLoader.Services;
using UnturnedModLoader.Services.Api;
using UnturnedModLoader.Views;

namespace UnturnedModLoader.ViewModels;

public enum MainPage
{
    Browse,
    Installed,
}

public partial class MainViewModel : ViewModelBase
{
    private readonly SettingsService _settingsService;
    private readonly AppSettings _settings;
    private readonly FolderPickerService _folderPicker;
    private readonly IModsApiClient _modsApi;
    private readonly AuthSessionService _session;
    private readonly RemoteImageService _imageService;
    private readonly InstalledModsService _installedModsService;
    private readonly ProfileService _profileService;
    private readonly VirtualFilesystemService _vfs;
    private readonly ModDownloadService _downloadService;
    private readonly Window _owner;
    private CancellationTokenSource? _searchDebounceCts;
    private CancellationTokenSource? _loadCts;
    private bool _wasGameRunning;
    private DispatcherTimer? _gameProcessTimer;

    [ObservableProperty]
    private MainPage _currentPage = MainPage.Browse;

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private string _gamePath = "";

    [ObservableProperty]
    private string? _selectedCategoryKey;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isEmpty;

    [ObservableProperty]
    private string _listMessage = "";

    [ObservableProperty]
    private string _emptyStateTitle = "";

    [ObservableProperty]
    private string _emptyStateSubtitle = "";

    [ObservableProperty]
    private bool _isErrorState;

    [ObservableProperty]
    private bool _isSearchEmpty;

    [ObservableProperty]
    private bool _isGameRunning;

    [ObservableProperty]
    private string _activeProfileName = "";

    [ObservableProperty]
    private string _activeProfileId = GameProfile.DefaultBuiltInId;

    public ObservableCollection<ModItemViewModel> Mods { get; } = [];
    public ObservableCollection<InstalledModItemViewModel> InstalledMods { get; } = [];
    public ObservableCollection<CategoryViewModel> Categories { get; } = [];
    public ObservableCollection<ProfileItemViewModel> Profiles { get; } = [];

    public bool IsBrowsePage => CurrentPage == MainPage.Browse;
    public bool IsInstalledPage => CurrentPage == MainPage.Installed;

    public int TotalCount => IsInstalledPage ? InstalledMods.Count : Mods.Count;
    public string Username => _settings.Username ?? "";

    public bool ShowPackageEmptyIcon => IsEmpty && !IsErrorState && !IsSearchEmpty;
    public bool ShowSearchEmptyIcon => IsEmpty && IsSearchEmpty && !IsErrorState;
    public bool ShowErrorEmptyIcon => IsEmpty && IsErrorState;

    /// <summary>Whether the WinFsp virtual drive is mounted, for the launch-button indicator.</summary>
    public bool IsVfsMounted => _vfs.IsMounted;

    public event Action? OnboardingResetRequested;

    /// <summary>Human-readable mount status shown next to the launch button.</summary>
    public string VfsMountStatus =>
        _vfs.IsMounted ? L.Get(Main.MountedAt, _vfs.MountPoint ?? "") : L.Get(Main.NotMounted);

    public MainViewModel(
        SettingsService settingsService,
        AppSettings settings,
        FolderPickerService folderPicker,
        IModsApiClient modsApi,
        AuthSessionService session,
        RemoteImageService imageService,
        InstalledModsService installedModsService,
        ProfileService profileService,
        VirtualFilesystemService vfs,
        ModDownloadService downloadService,
        Window owner)
    {
        _settingsService = settingsService;
        _settings = settings;
        _folderPicker = folderPicker;
        _modsApi = modsApi;
        _session = session;
        _imageService = imageService;
        _installedModsService = installedModsService;
        _profileService = profileService;
        _vfs = vfs;
        _downloadService = downloadService;
        _owner = owner;
        _gamePath = settings.GamePath;
        _statusText = L.Get(Main.Ready);

        RefreshProfileList();
        UpdateStatus();
        StartGameProcessMonitoring();
        NotifyVfsMountChanged();
        _ = InitializeAsync();
    }

    private void NotifyVfsMountChanged()
    {
        OnPropertyChanged(nameof(IsVfsMounted));
        OnPropertyChanged(nameof(VfsMountStatus));
    }

    [RelayCommand]
    private async Task SelectPageAsync(string? pageName)
    {
        var page = pageName?.ToLowerInvariant() switch
        {
            "installed" => MainPage.Installed,
            _ => MainPage.Browse,
        };

        if (page == CurrentPage)
            return;

        CurrentPage = page;
        NotifyPageStates();

        // Always keep process monitoring so runtime capture can finish after the game exits,
        // even if the user is on the Browse page.
        StartGameProcessMonitoring();

        if (page == MainPage.Browse)
            await LoadModsAsync();
        else
            await LoadInstalledModsAsync();
    }

    [RelayCommand]
    private async Task RefreshModsAsync()
    {
        if (IsInstalledPage)
        {
            StartGameProcessMonitoring();
            await LoadInstalledModsAsync();
        }
        else
        {
            await LoadCategoriesAsync();
            await LoadModsAsync();
        }
    }

    [RelayCommand]
    private async Task SelectCategoryAsync(string? categoryKey)
    {
        if (!IsBrowsePage)
            return;

        var normalizedKey = string.IsNullOrWhiteSpace(categoryKey) ? null : categoryKey;
        if (normalizedKey == SelectedCategoryKey)
            return;

        SelectedCategoryKey = normalizedKey;
        UpdateCategorySelection();
        await LoadModsAsync();
    }

    [RelayCommand]
    private async Task OpenModDetailsAsync(ModItemViewModel? mod)
    {
        if (mod is null)
            return;
        await OpenModDetailsByIdAsync(mod.Id, autoInstall: false);
    }

    /// <summary>
    /// Handle a browser-launched install intent (<c>unmod://install/{id}</c>): open the mod
    /// detail dialog and auto-start the install once details load.
    /// </summary>
    public void ConsumePendingInstall(int modId) => _ = OpenModDetailsByIdAsync(modId, autoInstall: true);

    private async Task OpenModDetailsByIdAsync(int modId, bool autoInstall)
    {
        var viewModel = new ModDetailViewModel(
            _imageService,
            _modsApi.BaseUrl,
            _modsApi,
            _settings,
            _owner,
            downloadService: _downloadService,
            getInstallModulesFolder: () => _installedModsService.OverlayRoot)
        {
            AutoInstallAfterLoad = autoInstall,
        };

        var dialog = new ModDetailWindow { DataContext = viewModel };
        var downloadCompleted = false;

        viewModel.CloseRequested += () => dialog.Close();
        viewModel.DownloadCompleted += () => downloadCompleted = true;
        _ = viewModel.LoadAsync(modId);
        await dialog.ShowDialog(_owner);

        if (downloadCompleted)
        {
            if (IsInstalledPage)
                await LoadInstalledModsAsync();
        }
    }

    [RelayCommand]
    private async Task OpenInstalledModDetailsAsync(InstalledModItemViewModel? mod)
    {
        if (mod is null)
            return;

        var viewModel = new InstalledModDetailViewModel(_imageService, _modsApi.BaseUrl);
        var dialog = new InstalledModDetailWindow { DataContext = viewModel };

        viewModel.CloseRequested += () => dialog.Close();
        viewModel.Load(mod);
        await dialog.ShowDialog(_owner);
    }

    [RelayCommand]
    private async Task RemoveInstalledModAsync(InstalledModItemViewModel? mod)
    {
        if (mod is null || mod.RemoteId is null)
            return;

        if (CheckIfGameIsRunning())
        {
            await DialogService.ConfirmAsync(
                _owner,
                L.Get(I18n.Common.Confirm),
                L.Get(I18n.Main.GameRunningDeleteBlocked),
                confirmText: L.Get(I18n.Common.Confirm),
                cancelText: ""
            );
            return;
        }

        if (!_installedModsService.Remove(mod.RemoteId.Value))
            return;

        InstalledMods.Remove(mod);
        OnPropertyChanged(nameof(TotalCount));
        StatusText = L.Get(Main.InstalledRemoved, mod.Title);

        if (InstalledMods.Count == 0)
        {
            SetEmptyState(
                isError: false,
                isSearch: false,
                title: L.Get(Main.NoInstalledTitle),
                subtitle: L.Get(Main.NoInstalledHint));
        }
    }

    [RelayCommand]
    private async Task OpenSettingsAsync(string? section = null)
    {
        var viewModel = new SettingsViewModel(
            _settingsService,
            _settings,
            _folderPicker,
            _session,
            _profileService,
            _vfs,
            _owner,
            initialSection: section);

        var dialog = new SettingsWindow { DataContext = viewModel };

        bool onboardingReset = false;
        viewModel.CloseRequested += () => dialog.Close();
        viewModel.OnboardingResetRequested += () =>
        {
            onboardingReset = true;
            dialog.Close();
        };

        await dialog.ShowDialog(_owner);

        if (onboardingReset)
        {
            OnboardingResetRequested?.Invoke();
            return;
        }

        GamePath = _settings.GamePath;
        OnPropertyChanged(nameof(Username));
        BindActiveProfile(_profileService.GetActive());
        RefreshProfileList();
        UpdateStatus();

        if (IsInstalledPage)
            await LoadInstalledModsAsync();
        else
        {
            await LoadCategoriesAsync();
            await LoadModsAsync();
        }
    }

    [RelayCommand]
    private async Task ManageProfilesAsync() => await OpenSettingsAsync("profiles");

    [RelayCommand]
    private void OpenVirtualDirectory()
    {
        var mountPoint = _vfs.MountPoint;
        if (string.IsNullOrWhiteSpace(mountPoint))
        {
            StatusText = L.Get(Main.VirtualDriveNotMounted);
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = mountPoint + Path.DirectorySeparatorChar,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    [RelayCommand]
    private async Task SelectProfileAsync(string? profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId))
            return;

        if (string.Equals(profileId, ActiveProfileId, StringComparison.OrdinalIgnoreCase))
            return;

        if (IsGameRunning || GameProcessService.IsRunning(GamePath))
        {
            StatusText = L.Get(ProfileKeys.GameRunning);
            return;
        }

        var result = await Task.Run(() => _profileService.SetActive(profileId));
        if (!result.Success)
        {
            StatusText = L.Get(Main.ProfileSwitchFailed, result.Error ?? "");
            return;
        }

        var active = _profileService.GetActive();
        BindActiveProfile(active);
        RefreshProfileList();
        StatusText = L.Get(Main.ProfileSwitched, active.Name);

        if (IsInstalledPage)
            await LoadInstalledModsAsync();
    }

    [RelayCommand]
    private void LaunchGame()
    {
        if (!GamePathValidator.IsValid(GamePath))
        {
            StatusText = L.Get(Main.GamePathNotConfigured);
            return;
        }

        // The game launches from the virtual drive, so WinFsp + a mounted volume are required.
        var winFspState = WinFspService.GetStatus().State;
        if (winFspState != WinFspInstallState.Installed)
        {
            StatusText = L.Get(Main.WinFspRequired);
            return;
        }

        if (!_vfs.IsMounted)
        {
            StatusText = L.Get(Main.VirtualDriveNotMounted);
            return;
        }

        var active = _profileService.GetActive();
        // Ensure the active profile's overlay is bound to the volume (no-op if already current).
        _vfs.SetLowerRoot(GamePath);
        _vfs.SetActiveOverlay(active.Id);

        if (!GameProcessService.IsRunning(GamePath, _vfs.DriveLetter))
        {
            if (!GameProcessService.TryLaunchFromVirtualDrive(_vfs.DriveLetter, out var error))
            {
                StatusText = L.Get(Main.LaunchFailed, error ?? "");
                return;
            }
        }

        StatusText = L.Get(Main.LaunchStarted);
        IsGameRunning = true;
        _wasGameRunning = true;
        StartGameProcessMonitoring();
    }

    private void RefreshProfileList()
    {
        Profiles.Clear();
        var activeId = _profileService.ActiveProfileId;
        foreach (var profile in _profileService.List())
            Profiles.Add(ProfileItemViewModel.From(profile, activeId));

        BindActiveProfile(_profileService.GetActive());
    }

    private void BindActiveProfile(GameProfile profile)
    {
        ActiveProfileId = profile.Id;
        ActiveProfileName = profile.Name;
        _installedModsService.UseProfile(profile.Id);
    }

    partial void OnCurrentPageChanged(MainPage value) => NotifyPageStates();

    partial void OnGamePathChanged(string value)
    {
        if (GamePathValidator.IsValid(value))
        {
            _settings.GamePath = value;
            _settingsService.Save(_settings);
        }

        UpdateStatus();
    }

    partial void OnSearchTextChanged(string value)
    {
        if (IsBrowsePage)
            DebounceSearch();
        else
            FilterInstalledMods();
    }

    private void DebounceSearch()
    {
        _searchDebounceCts?.Cancel();
        _searchDebounceCts = new CancellationTokenSource();
        _ = DebounceSearchAsync(_searchDebounceCts.Token);
    }

    private async Task DebounceSearchAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(400, token);
            await LoadModsAsync();
        }
        catch (TaskCanceledException)
        {
            // Ignore debounce cancellation.
        }
    }

    private async Task InitializeAsync()
    {
        if (_settings.IsLoggedIn)
            await _session.TryRestoreSessionAsync();

        OnPropertyChanged(nameof(Username));
        await LoadCategoriesAsync();
        await LoadModsAsync();

        // Background update check on startup
        _ = Task.Run(async () =>
        {
            try
            {
                var release = await UpdateService.CheckForUpdatesAsync();
                if (release != null && UpdateService.IsNewerVersion(release.TagName, out _))
                {
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
                    {
                        var title = L.Get(I18n.Settings.UpdateDialogTitle);
                        var message = L.Get(I18n.Settings.UpdateDialogMessage, release.TagName, release.Body);
                        var confirm = await DialogService.ConfirmAsync(
                            _owner,
                            title,
                            message,
                            confirmText: L.Get(I18n.Settings.GoToDownload),
                            cancelText: L.Get(I18n.Common.Cancel)
                        );

                        if (confirm)
                        {
                            var url = release.HtmlUrl;
                            if (!string.IsNullOrWhiteSpace(url))
                            {
                                var psi = new System.Diagnostics.ProcessStartInfo
                                {
                                    FileName = url,
                                    UseShellExecute = true
                                };
                                System.Diagnostics.Process.Start(psi);
                            }
                        }
                    });
                }
            }
            catch
            {
                // Ignore startup check errors
            }
        });
    }

    private async Task LoadCategoriesAsync(CancellationToken cancellationToken = default)
    {
        var result = await _modsApi.GetCategoriesAsync(cancellationToken);
        if (!result.Success)
        {
            if (Categories.Count == 0)
            {
                Categories.Add(new CategoryViewModel(null, ModCategoryMapper.GetAllCategoryIcon())
                {
                    IsSelected = SelectedCategoryKey is null,
                });
            }

            return;
        }

        ModCategoryMapper.SetCategories(result.Categories);
        ApplyCategories(result.Categories);
    }

    private void ApplyCategories(IReadOnlyList<RemoteCategory> remoteCategories)
    {
        var previousSelection = SelectedCategoryKey;
        Categories.Clear();

        Categories.Add(new CategoryViewModel(null, ModCategoryMapper.GetAllCategoryIcon())
        {
            IsSelected = previousSelection is null,
        });

        foreach (var category in remoteCategories.OrderBy(c => c.SortOrder).ThenBy(c => c.Id))
        {
            Categories.Add(new CategoryViewModel(
                category.Key,
                ModCategoryMapper.GetIconFromApiName(category.Icon))
            {
                IsSelected = category.Key == previousSelection,
            });
        }

        if (!Categories.Any(c => c.IsSelected))
        {
            SelectedCategoryKey = null;
            UpdateCategorySelection();
        }
    }

    private void UpdateCategorySelection()
    {
        foreach (var category in Categories)
            category.IsSelected = category.Key == SelectedCategoryKey;
    }

    private async Task LoadModsAsync()
    {
        if (!IsBrowsePage)
            return;

        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var token = _loadCts.Token;

        IsLoading = true;
        IsEmpty = false;
        ListMessage = L.Get(Main.LoadingMods);

        try
        {
            var query = new ModsQuery
            {
                Category = SelectedCategoryKey,
                Search = SearchText,
            };

            var result = await _modsApi.GetModsAsync(query, token);
            if (!result.Success)
            {
                Mods.Clear();
                SetEmptyState(
                    isError: true,
                    isSearch: false,
                    title: L.Get(Main.CannotConnectTitle),
                    subtitle: result.Error ?? L.Get(Main.CannotConnectHint));
                StatusText = result.Error ?? L.Get(Main.ApiRequestFailed);
                OnPropertyChanged(nameof(TotalCount));
                return;
            }

            await ApplyModsAsync(result.Mods, token);
            UpdateCategoryCounts(result.Mods, result.Total);

            if (Mods.Count == 0)
            {
                var hasFilter = !string.IsNullOrWhiteSpace(SearchText) || SelectedCategoryKey is not null;
                SetEmptyState(
                    isError: false,
                    isSearch: hasFilter,
                    title: hasFilter ? L.Get(Main.NoMatchTitle) : L.Get(Main.NoModsTitle),
                    subtitle: hasFilter ? L.Get(Main.NoMatchHint) : L.Get(Main.NoModsHint));
                StatusText = L.Get(Main.ConnectedEmpty, _modsApi.BaseUrl);
            }
            else
            {
                IsEmpty = false;
                IsErrorState = false;
                IsSearchEmpty = false;
                ListMessage = "";
                StatusText = L.Get(Main.LoadedCount, result.Total, _modsApi.BaseUrl);
                NotifyEmptyIconStates();
            }
        }
        catch (TaskCanceledException)
        {
            // Superseded by a newer request.
        }
        finally
        {
            if (!token.IsCancellationRequested)
                IsLoading = false;
        }
    }

    private async Task LoadInstalledModsAsync()
    {
        if (!IsInstalledPage)
            return;

        IsLoading = true;
        IsEmpty = false;
        ListMessage = L.Get(Main.LoadingInstalled);

        try
        {
            if (!GamePathValidator.IsValid(GamePath))
            {
                InstalledMods.Clear();
                SetEmptyState(
                    isError: false,
                    isSearch: false,
                    title: L.Get(Main.GamePathNotConfigured),
                    subtitle: L.Get(Main.NoInstalledHint));
                StatusText = L.Get(Main.GamePathNotConfigured);
                OnPropertyChanged(nameof(TotalCount));
                return;
            }

            var installed = await Task.Run(() => _installedModsService.GetAll());
            InstalledMods.Clear();

            foreach (var mod in installed)
            {
                if (!MatchesInstalledSearch(mod))
                    continue;

                var vm = new InstalledModItemViewModel
                {
                    RemoteId = mod.RemoteId,
                    Title = mod.Title,
                    Author = mod.Author ?? "-",
                    Version = string.IsNullOrWhiteSpace(mod.Version) ? "-" : mod.Version,
                    Category = mod.Category ?? "",
                    Description = string.IsNullOrWhiteSpace(mod.Description)
                        ? L.Get(Common.NoDescription)
                        : mod.Description,
                    CoverUrl = mod.CoverUrl,
                    FileCount = mod.Files.Count,
                };

                InstalledMods.Add(vm);
                _ = LoadInstalledCoverAsync(vm);
            }

            if (InstalledMods.Count == 0)
            {
                var hasSearch = !string.IsNullOrWhiteSpace(SearchText);
                SetEmptyState(
                    isError: false,
                    isSearch: hasSearch,
                    title: hasSearch ? L.Get(Main.NoMatchTitle) : L.Get(Main.NoInstalledTitle),
                    subtitle: hasSearch
                        ? L.Get(Main.NoMatchHint)
                        : L.Get(Main.NoInstalledHint));
                StatusText = L.Get(Main.InstalledCount, 0);
            }
            else
            {
                IsEmpty = false;
                IsErrorState = false;
                IsSearchEmpty = false;
                ListMessage = "";
                StatusText = L.Get(Main.InstalledCount, InstalledMods.Count);
                NotifyEmptyIconStates();
            }

            OnPropertyChanged(nameof(TotalCount));
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void FilterInstalledMods() => _ = LoadInstalledModsAsync();

    private void StartGameProcessMonitoring()
    {
        _gameProcessTimer ??= new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2),
        };

        _gameProcessTimer.Tick -= OnGameProcessTimerTick;
        _gameProcessTimer.Tick += OnGameProcessTimerTick;
        _gameProcessTimer.Start();
        RefreshGameRunningState();
    }

    private void StopGameProcessMonitoring()
    {
        if (_gameProcessTimer is null)
            return;

        _gameProcessTimer.Stop();
        _gameProcessTimer.Tick -= OnGameProcessTimerTick;

        _wasGameRunning = false;

        if (!IsGameRunning)
            return;

        IsGameRunning = false;
    }

    private void OnGameProcessTimerTick(object? sender, EventArgs e) => RefreshGameRunningState();

    private void RefreshGameRunningState()
    {
        var running = GameProcessService.IsRunning(GamePath, _vfs.DriveLetter);

        if (running == IsGameRunning)
            return;

        _wasGameRunning = running;
        IsGameRunning = running;

        if (IsInstalledPage && !IsLoading)
            UpdateStatus();
    }

    public char VfsDriveLetter => _vfs.DriveLetter;

    public bool CheckIfGameIsRunning()
    {
        return IsGameRunning || GameProcessService.IsRunning(GamePath, _vfs.DriveLetter);
    }

    private bool MatchesInstalledSearch(InstalledMod mod)
    {
        if (string.IsNullOrWhiteSpace(SearchText))
            return true;

        var query = SearchText.Trim();
        return mod.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
               || (mod.Author?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
               || (mod.Category?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private async Task ApplyModsAsync(IReadOnlyList<RemoteMod> remoteMods, CancellationToken token)
    {
        Mods.Clear();
        var locale = LocalizationService.CurrentLocaleCode;

        foreach (var remote in remoteMods)
        {
            var description = LocalizedContent.Pick(remote.Description, locale);
            var vm = new ModItemViewModel
            {
                Id = remote.Id,
                Name = LocalizedContent.Pick(remote.Title, locale),
                Author = remote.AuthorName,
                Version = string.IsNullOrWhiteSpace(remote.Version) ? "—" : remote.Version,
                Category = ModCategoryMapper.ToLabel(remote.Category),
                Description = string.IsNullOrWhiteSpace(description)
                    ? L.Get(Common.NoDescription)
                    : MarkdownTextHelper.StripForPreview(description),
                CoverUrl = remote.CoverUrl,
                FileUrl = remote.FileUrl,
                Downloads = remote.Downloads,
                LikeCount = remote.LikeCount,
            };

            Mods.Add(vm);
            _ = LoadBrowseCoverAsync(vm, remote.CoverUrl, token);
        }

        OnPropertyChanged(nameof(TotalCount));
    }

    private async Task LoadBrowseCoverAsync(
        ModItemViewModel vm,
        string? coverUrl,
        CancellationToken token)
    {
        var resolved = RemoteImageService.ResolveUrl(_modsApi.BaseUrl, coverUrl);
        var bitmap = await _imageService.LoadAsync(resolved, token);
        if (token.IsCancellationRequested)
            return;

        vm.CoverImage = bitmap;
        vm.HasCoverImage = bitmap is not null;
    }

    private async Task LoadInstalledCoverAsync(InstalledModItemViewModel vm)
    {
        if (string.IsNullOrWhiteSpace(vm.CoverUrl))
        {
            vm.HasCoverImage = false;
            return;
        }

        var resolved = RemoteImageService.ResolveUrl(_modsApi.BaseUrl, vm.CoverUrl);
        var bitmap = await _imageService.LoadAsync(resolved);
        vm.CoverImage = bitmap;
        vm.HasCoverImage = bitmap is not null;
    }

    private void UpdateCategoryCounts(IReadOnlyList<RemoteMod> mods, int apiTotal)
    {
        if (SelectedCategoryKey is null)
        {
            var counts = mods
                .GroupBy(m => m.Category, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

            foreach (var category in Categories)
            {
                category.Count = category.Key is null
                    ? apiTotal
                    : counts.TryGetValue(category.Key, out var count) ? count : 0;
            }

            return;
        }

        foreach (var category in Categories)
            category.Count = category.Key == SelectedCategoryKey ? apiTotal : 0;
    }

    private void SetEmptyState(bool isError, bool isSearch, string title, string subtitle)
    {
        IsEmpty = true;
        IsErrorState = isError;
        IsSearchEmpty = isSearch;
        EmptyStateTitle = title;
        EmptyStateSubtitle = subtitle;
        ListMessage = subtitle;
        NotifyEmptyIconStates();
    }

    partial void OnIsEmptyChanged(bool value) => NotifyEmptyIconStates();
    partial void OnIsErrorStateChanged(bool value) => NotifyEmptyIconStates();
    partial void OnIsSearchEmptyChanged(bool value) => NotifyEmptyIconStates();

    private void NotifyEmptyIconStates()
    {
        OnPropertyChanged(nameof(ShowPackageEmptyIcon));
        OnPropertyChanged(nameof(ShowSearchEmptyIcon));
        OnPropertyChanged(nameof(ShowErrorEmptyIcon));
    }

    private void NotifyPageStates()
    {
        OnPropertyChanged(nameof(IsBrowsePage));
        OnPropertyChanged(nameof(IsInstalledPage));
        OnPropertyChanged(nameof(TotalCount));
    }

    private void UpdateStatus()
    {
        if (IsLoading)
            return;

        if (!string.IsNullOrWhiteSpace(ListMessage) && IsEmpty)
            return;

        if (IsInstalledPage)
        {
            if (IsGameRunning)
            {
                StatusText = L.Get(Main.GameRunningToggleBlocked);
                return;
            }

            StatusText = GamePathValidator.IsValid(GamePath)
                ? L.Get(Main.InstalledCount, InstalledMods.Count)
                : L.Get(Main.GamePathNotConfigured);
            return;
        }

        StatusText = GamePathValidator.IsValid(GamePath)
            ? L.Get(Main.Connected, _modsApi.BaseUrl)
            : string.IsNullOrWhiteSpace(GamePath)
                ? L.Get(Main.GamePathNotConfigured)
                : L.Get(Main.GamePathInvalid);
    }

    protected override void OnLocalizationChanged()
    {
        RefreshProfileList();
        NotifyVfsMountChanged();
        _ = RefreshModsAsync();
    }
}

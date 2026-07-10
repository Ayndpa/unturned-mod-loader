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
    private readonly Window _owner;
    private CancellationTokenSource? _searchDebounceCts;
    private CancellationTokenSource? _loadCts;
    private bool _isHandlingToggle;
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

    public string? GameRunningToggleTooltip =>
        IsGameRunning ? L.Get(Main.GameRunningToggleBlocked) : null;

    public ObservableCollection<ModItemViewModel> Mods { get; } = [];
    public ObservableCollection<InstalledModItemViewModel> InstalledMods { get; } = [];
    public ObservableCollection<CategoryViewModel> Categories { get; } = [];

    public bool IsBrowsePage => CurrentPage == MainPage.Browse;
    public bool IsInstalledPage => CurrentPage == MainPage.Installed;

    public int EnabledCount => InstalledMods.Count(m => m.IsEnabled);

    public int TotalCount => IsInstalledPage ? InstalledMods.Count : Mods.Count;
    public string Username => _settings.Username ?? "";

    public bool ShowPackageEmptyIcon => IsEmpty && !IsErrorState && !IsSearchEmpty;
    public bool ShowSearchEmptyIcon => IsEmpty && IsSearchEmpty && !IsErrorState;
    public bool ShowErrorEmptyIcon => IsEmpty && IsErrorState;

    public MainViewModel(
        SettingsService settingsService,
        AppSettings settings,
        FolderPickerService folderPicker,
        IModsApiClient modsApi,
        AuthSessionService session,
        RemoteImageService imageService,
        InstalledModsService installedModsService,
        Window owner)
    {
        _settingsService = settingsService;
        _settings = settings;
        _folderPicker = folderPicker;
        _modsApi = modsApi;
        _session = session;
        _imageService = imageService;
        _installedModsService = installedModsService;
        _owner = owner;
        _gamePath = settings.GamePath;
        _statusText = L.Get(Main.Ready);

        UpdateStatus();
        _ = InitializeAsync();
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

        if (page == MainPage.Browse)
        {
            StopGameProcessMonitoring();
            await LoadModsAsync();
        }
        else
        {
            StartGameProcessMonitoring();
            await LoadInstalledModsAsync();
        }
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

        var viewModel = new ModDetailViewModel(
            _imageService,
            _modsApi.BaseUrl,
            _modsApi,
            _settings,
            _owner);
        var dialog = new ModDetailWindow { DataContext = viewModel };
        var downloadCompleted = false;

        viewModel.CloseRequested += () => dialog.Close();
        viewModel.DownloadCompleted += () => downloadCompleted = true;
        _ = viewModel.LoadAsync(mod.Id);
        await dialog.ShowDialog(_owner);

        if (downloadCompleted && GamePathValidator.IsValid(GamePath))
        {
            _installedModsService.ScanAndMerge(GamePath);
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
    private void RemoveInstalledMod(InstalledModItemViewModel? mod)
    {
        if (mod is null)
            return;

        if (!_installedModsService.Remove(mod.RelativePath, GamePath))
            return;

        InstalledMods.Remove(mod);
        OnPropertyChanged(nameof(EnabledCount));
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
    private void OpenModsFolder()
    {
        if (!GamePathValidator.IsValid(GamePath))
        {
            StatusText = L.Get(Main.GamePathInvalid);
            return;
        }

        _installedModsService.OpenModsFolder(GamePath);
    }

    [RelayCommand]
    private void OpenGameFolder()
    {
        if (!GamePathValidator.IsValid(GamePath))
        {
            StatusText = L.Get(Main.GamePathInvalid);
            return;
        }

        _installedModsService.OpenGameFolder(GamePath);
    }

    [RelayCommand]
    private async Task OpenSettingsAsync()
    {
        var viewModel = new SettingsViewModel(
            _settingsService,
            _settings,
            _folderPicker,
            _session,
            _owner);

        var dialog = new SettingsWindow { DataContext = viewModel };

        viewModel.CloseRequested += () => dialog.Close();

        await dialog.ShowDialog(_owner);

        GamePath = _settings.GamePath;
        OnPropertyChanged(nameof(Username));
        UpdateStatus();

        if (IsInstalledPage)
            await LoadInstalledModsAsync();
        else
        {
            await LoadCategoriesAsync();
            await LoadModsAsync();
        }
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
                OnPropertyChanged(nameof(EnabledCount));
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
        ListMessage = L.Get(Main.ParsingInstalled);

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
                OnPropertyChanged(nameof(EnabledCount));
                OnPropertyChanged(nameof(TotalCount));
                return;
            }

            var installed = _installedModsService.ScanAndMerge(GamePath);
            InstalledMods.Clear();

            foreach (var mod in installed.OrderByDescending(m => m.InstalledAt))
            {
                if (!MatchesInstalledSearch(mod))
                    continue;

                var vm = new InstalledModItemViewModel
                {
                    RemoteId = mod.RemoteId,
                    Kind = mod.Kind,
                    Title = mod.Title,
                    ModuleName = mod.ModuleName,
                    Author = mod.Author ?? "—",
                    Version = string.IsNullOrWhiteSpace(mod.Version) ? "—" : mod.Version,
                    Category = mod.Kind == LocalModKind.Module
                        ? L.Get(InstalledModDetail.TypeModule)
                        : L.Get(InstalledModDetail.TypeDll),
                    Description = string.IsNullOrWhiteSpace(mod.Description)
                        ? L.Get(Common.NoDescription)
                        : mod.Description,
                    RelativePath = mod.RelativePath,
                    ModuleFilePath = mod.ModuleFilePath,
                    DirectoryPath = mod.DirectoryPath,
                    LocalIconPath = mod.LocalIconPath,
                    CoverUrl = mod.CoverUrl,
                    DependencyNames = mod.DependencyNames,
                    Dependencies = mod.Dependencies,
                    Assemblies = mod.Assemblies,
                    IsEnabled = mod.IsEnabled,
                };

                vm.PropertyChanged += async (_, e) =>
                {
                    if (e.PropertyName == nameof(InstalledModItemViewModel.IsEnabled))
                        await HandleInstalledModToggleAsync(vm);
                };

                InstalledMods.Add(vm);
                _ = LoadInstalledCoverAsync(vm);
            }

            UpdateInstalledModToggleAvailability();

            if (InstalledMods.Count == 0)
            {
                var hasSearch = !string.IsNullOrWhiteSpace(SearchText);
                SetEmptyState(
                    isError: false,
                    isSearch: hasSearch,
                    title: hasSearch ? L.Get(Main.NoMatchTitle) : L.Get(Main.NoInstalledTitle),
                    subtitle: hasSearch ? L.Get(Main.NoMatchHint) : L.Get(Main.NoInstalledHint));
                StatusText = L.Get(Main.InstalledEnabledSummary, 0, 0);
            }
            else
            {
                IsEmpty = false;
                IsErrorState = false;
                IsSearchEmpty = false;
                ListMessage = "";
                StatusText = L.Get(Main.InstalledEnabledSummary, EnabledCount, InstalledMods.Count);
                NotifyEmptyIconStates();
            }

            OnPropertyChanged(nameof(EnabledCount));
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

        if (!IsGameRunning)
            return;

        IsGameRunning = false;
        UpdateInstalledModToggleAvailability();
        OnPropertyChanged(nameof(GameRunningToggleTooltip));
    }

    private void OnGameProcessTimerTick(object? sender, EventArgs e) => RefreshGameRunningState();

    private void RefreshGameRunningState()
    {
        var running = GameProcessService.IsRunning(GamePath);
        if (running == IsGameRunning)
            return;

        IsGameRunning = running;
        UpdateInstalledModToggleAvailability();
        OnPropertyChanged(nameof(GameRunningToggleTooltip));

        if (IsInstalledPage && !IsLoading)
            UpdateStatus();
    }

    private void UpdateInstalledModToggleAvailability()
    {
        var canToggle = !IsGameRunning;
        foreach (var vm in InstalledMods)
            vm.CanToggle = canToggle;
    }

    partial void OnIsGameRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(GameRunningToggleTooltip));
        UpdateInstalledModToggleAvailability();
    }

    private async Task HandleInstalledModToggleAsync(InstalledModItemViewModel vm)
    {
        if (_isHandlingToggle)
            return;

        if (IsGameRunning)
        {
            RevertToggle(vm, !vm.IsEnabled);
            StatusText = L.Get(Main.GameRunningToggleBlocked);
            return;
        }

        var desiredEnabled = vm.IsEnabled;
        var installedMods = _installedModsService.GetAll();
        var installedMod = installedMods.FirstOrDefault(mod =>
            string.Equals(mod.RelativePath, vm.RelativePath, StringComparison.OrdinalIgnoreCase));

        if (installedMod is null)
        {
            RevertToggle(vm, !desiredEnabled);
            StatusText = L.Get(Main.ModToggleFailed);
            return;
        }

        _isHandlingToggle = true;
        vm.IsTogglePending = true;

        try
        {
            var pathsToEnable = new List<string>();
            var pathsToDisable = new List<string>();

            if (desiredEnabled)
            {
                pathsToEnable.AddRange(await BuildEnablePathsAsync(installedMod, installedMods));
                if (pathsToEnable.Count == 0)
                {
                    RevertToggle(vm, false);
                    return;
                }
            }
            else
            {
                if (!await ConfirmDisableAsync(installedMod, installedMods, pathsToDisable))
                {
                    RevertToggle(vm, true);
                    return;
                }

                pathsToDisable.Add(installedMod.RelativePath);
            }

            if (desiredEnabled)
            {
                if (!_installedModsService.SetEnabledMany(pathsToEnable, GamePath, enabled: true))
                {
                    RevertToggle(vm, false);
                    StatusText = L.Get(Main.ModToggleFailed);
                    return;
                }

                ApplyEnabledStateToViewModels(pathsToEnable, enabled: true);
                StatusText = L.Get(Main.ModToggleEnabled, installedMod.Title);
            }
            else
            {
                if (!_installedModsService.SetEnabledMany(pathsToDisable, GamePath, enabled: false))
                {
                    RevertToggle(vm, true);
                    StatusText = L.Get(Main.ModToggleFailed);
                    return;
                }

                ApplyEnabledStateToViewModels(pathsToDisable, enabled: false);
                StatusText = L.Get(Main.ModToggleDisabled, installedMod.Title);
            }

            OnPropertyChanged(nameof(EnabledCount));
        }
        finally
        {
            vm.IsTogglePending = false;
            _isHandlingToggle = false;
        }
    }

    private async Task<IReadOnlyList<string>> BuildEnablePathsAsync(
        InstalledMod mod,
        IReadOnlyList<InstalledMod> installedMods)
    {
        var paths = new List<string>();

        if (mod.Kind == LocalModKind.Module)
        {
            var graph = ModuleDependencyGraph.Build(installedMods);
            var dependencies = graph.ExpandDependenciesForEnable(mod, [mod.RelativePath]);
            if (dependencies.Count > 0)
            {
                var names = dependencies
                    .Select(dependency => dependency.Title)
                    .Distinct(StringComparer.OrdinalIgnoreCase);

                var confirmed = await DialogService.ConfirmAsync(
                    _owner,
                    L.Get(Main.EnableDependencyTitle),
                    L.Get(Main.EnableDependencyMessage, string.Join(Environment.NewLine, names)),
                    L.Get(Main.EnableWithDependencies));

                if (!confirmed)
                    return [];

                foreach (var dependency in dependencies)
                    paths.Add(dependency.RelativePath);
            }
        }

        paths.Add(mod.RelativePath);
        return paths;
    }

    private async Task<bool> ConfirmDisableAsync(
        InstalledMod mod,
        IReadOnlyList<InstalledMod> installedMods,
        List<string> pathsToDisable)
    {
        if (mod.Kind != LocalModKind.Module || string.IsNullOrWhiteSpace(mod.ModuleName))
            return true;

        var graph = ModuleDependencyGraph.Build(installedMods);
        var dependents = graph.GetEnabledDependents(mod);
        if (dependents.Count == 0)
            return true;

        foreach (var dependent in dependents.OrderByDescending(item => item.RelativePath, StringComparer.OrdinalIgnoreCase))
            pathsToDisable.Add(dependent.RelativePath);

        var names = dependents
            .Select(dependent => dependent.Title)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var message = L.Get(Main.DisableCascadeMessage, string.Join(Environment.NewLine, names));
        return await DialogService.ConfirmAsync(
            _owner,
            L.Get(Main.DisableCascadeTitle),
            message,
            L.Get(Common.Confirm));
    }

    private void RevertToggle(InstalledModItemViewModel vm, bool enabled)
    {
        _isHandlingToggle = true;
        vm.IsEnabled = enabled;
        _isHandlingToggle = false;
    }

    private void ApplyEnabledStateToViewModels(IEnumerable<string> relativePaths, bool enabled)
    {
        var targets = relativePaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _isHandlingToggle = true;
        try
        {
            foreach (var item in InstalledMods.Where(vm => targets.Contains(vm.RelativePath)))
                item.IsEnabled = enabled;
        }
        finally
        {
            _isHandlingToggle = false;
        }
    }

    private bool MatchesInstalledSearch(InstalledMod mod)
    {
        if (string.IsNullOrWhiteSpace(SearchText))
            return true;

        var query = SearchText.Trim();
        return mod.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
               || (mod.Author?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
               || mod.RelativePath.Contains(query, StringComparison.OrdinalIgnoreCase)
               || mod.Dependencies.Any(dep => dep.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private async Task ApplyModsAsync(IReadOnlyList<RemoteMod> remoteMods, CancellationToken token)
    {
        Mods.Clear();

        foreach (var remote in remoteMods)
        {
            var vm = new ModItemViewModel
            {
                Id = remote.Id,
                Name = remote.Title,
                Author = remote.AuthorName,
                Version = string.IsNullOrWhiteSpace(remote.Version) ? "—" : remote.Version,
                Category = ModCategoryMapper.ToLabel(remote.Category),
                Description = string.IsNullOrWhiteSpace(remote.Description)
                    ? L.Get(Common.NoDescription)
                    : MarkdownTextHelper.StripForPreview(remote.Description),
                CoverUrl = remote.CoverUrl,
                FileUrl = remote.FileUrl,
                Downloads = remote.Downloads,
                LikeCount = remote.LikeCount,
            };

            Mods.Add(vm);
            _ = LoadBrowseCoverAsync(vm, remote.CoverUrl, token);
        }

        OnPropertyChanged(nameof(EnabledCount));
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
        var localBitmap = RemoteImageService.LoadLocal(vm.LocalIconPath);
        if (localBitmap is not null)
        {
            vm.CoverImage = localBitmap;
            vm.HasCoverImage = true;
            return;
        }

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
        OnPropertyChanged(nameof(EnabledCount));
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
                ? L.Get(Main.InstalledEnabledSummary, EnabledCount, InstalledMods.Count)
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
        _ = RefreshModsAsync();
    }
}
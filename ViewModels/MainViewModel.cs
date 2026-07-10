using System.Collections.ObjectModel;
using Avalonia.Controls;
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
    private readonly Action? _onLogout;
    private readonly Dictionary<int, bool> _enabledStates = [];
    private CancellationTokenSource? _searchDebounceCts;
    private CancellationTokenSource? _loadCts;

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

    public ObservableCollection<ModItemViewModel> Mods { get; } = [];
    public ObservableCollection<InstalledModItemViewModel> InstalledMods { get; } = [];
    public ObservableCollection<CategoryViewModel> Categories { get; } = [];

    public bool IsBrowsePage => CurrentPage == MainPage.Browse;
    public bool IsInstalledPage => CurrentPage == MainPage.Installed;

    public int EnabledCount => IsInstalledPage
        ? InstalledMods.Count(m => m.IsEnabled)
        : Mods.Count(m => m.IsEnabled);

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
        Window owner,
        Action? onLogout = null)
    {
        _settingsService = settingsService;
        _settings = settings;
        _folderPicker = folderPicker;
        _modsApi = modsApi;
        _session = session;
        _imageService = imageService;
        _installedModsService = installedModsService;
        _owner = owner;
        _onLogout = onLogout;
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
            await LoadModsAsync();
        else
            await LoadInstalledModsAsync();
    }

    [RelayCommand]
    private async Task RefreshModsAsync()
    {
        if (IsInstalledPage)
            await LoadInstalledModsAsync();
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

        var viewModel = new ModDetailViewModel(_imageService, _modsApi.BaseUrl);
        var dialog = new ModDetailWindow { DataContext = viewModel };

        viewModel.CloseRequested += () => dialog.Close();
        _ = viewModel.LoadAsync(mod.Id, _modsApi);
        await dialog.ShowDialog(_owner);
    }

    [RelayCommand]
    private void RemoveInstalledMod(InstalledModItemViewModel? mod)
    {
        if (mod is null)
            return;

        if (!_installedModsService.Remove(mod.FileName, GamePath))
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
    private async Task OpenSettingsAsync()
    {
        var viewModel = new SettingsViewModel(
            _settingsService,
            _settings,
            _folderPicker,
            _session,
            _modsApi);

        var dialog = new SettingsWindow { DataContext = viewModel };
        var logoutTriggered = false;

        viewModel.CloseRequested += () => dialog.Close();
        viewModel.LogoutRequested += () =>
        {
            logoutTriggered = true;
            dialog.Close();
        };

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

        if (logoutTriggered)
            _onLogout?.Invoke();
    }

    [RelayCommand]
    private async Task BrowseGamePathAsync()
    {
        var picked = await _folderPicker.PickFolderAsync(
            L.Get(GamePathKeys.PickerTitle),
            string.IsNullOrWhiteSpace(GamePath) ? null : GamePath);

        if (picked is null)
            return;

        GamePath = picked;
        if (GamePathValidator.IsValid(GamePath))
        {
            _settings.GamePath = GamePath;
            _settingsService.Save(_settings);
            StatusText = L.Get(Main.GamePathUpdated);
        }
        else
        {
            StatusText = L.Get(GamePathKeys.Invalid, GamePathValidator.ExecutableName);
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

        foreach (var mod in Mods)
        {
            if (mod.IsEnabled)
                _enabledStates[mod.Id] = true;
        }

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
                OnPropertyChanged(nameof(EnabledCount));
                OnPropertyChanged(nameof(TotalCount));
                return;
            }

            _installedModsService.SyncWithFolder(GamePath);
            var installed = _installedModsService.GetAll();
            InstalledMods.Clear();

            foreach (var mod in installed.OrderByDescending(m => m.InstalledAt))
            {
                if (!MatchesInstalledSearch(mod))
                    continue;

                var vm = new InstalledModItemViewModel
                {
                    RemoteId = mod.RemoteId,
                    Title = mod.Title,
                    Author = mod.Author ?? "—",
                    Version = string.IsNullOrWhiteSpace(mod.Version) ? "—" : mod.Version,
                    Category = string.IsNullOrWhiteSpace(mod.Category)
                        ? L.Get(Category.Other)
                        : ModCategoryMapper.ToLabel(mod.Category),
                    Description = string.IsNullOrWhiteSpace(mod.Description)
                        ? L.Get(Common.NoDescription)
                        : mod.Description,
                    FileName = mod.FileName,
                    IsEnabled = mod.IsEnabled,
                };

                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(InstalledModItemViewModel.IsEnabled))
                    {
                        _installedModsService.SetEnabled(vm.FileName, vm.IsEnabled);
                        OnPropertyChanged(nameof(EnabledCount));
                    }
                };

                InstalledMods.Add(vm);
                _ = LoadInstalledCoverAsync(vm, mod.CoverUrl);
            }

            if (InstalledMods.Count == 0)
            {
                var hasSearch = !string.IsNullOrWhiteSpace(SearchText);
                SetEmptyState(
                    isError: false,
                    isSearch: hasSearch,
                    title: hasSearch ? L.Get(Main.NoMatchTitle) : L.Get(Main.NoInstalledTitle),
                    subtitle: hasSearch ? L.Get(Main.NoMatchHint) : L.Get(Main.NoInstalledHint));
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

            OnPropertyChanged(nameof(EnabledCount));
            OnPropertyChanged(nameof(TotalCount));
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void FilterInstalledMods() => _ = LoadInstalledModsAsync();

    private bool MatchesInstalledSearch(InstalledMod mod)
    {
        if (string.IsNullOrWhiteSpace(SearchText))
            return true;

        var query = SearchText.Trim();
        return mod.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
               || (mod.Author?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
               || mod.FileName.Contains(query, StringComparison.OrdinalIgnoreCase);
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
                    : remote.Description,
                CoverUrl = remote.CoverUrl,
                FileUrl = remote.FileUrl,
                Downloads = remote.Downloads,
                LikeCount = remote.LikeCount,
                IsEnabled = _enabledStates.TryGetValue(remote.Id, out var enabled) && enabled,
            };

            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ModItemViewModel.IsEnabled))
                {
                    _enabledStates[vm.Id] = vm.IsEnabled;
                    OnPropertyChanged(nameof(EnabledCount));
                }
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

    private async Task LoadInstalledCoverAsync(InstalledModItemViewModel vm, string? coverUrl)
    {
        var resolved = RemoteImageService.ResolveUrl(_modsApi.BaseUrl, coverUrl);
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
        _ = RefreshModsAsync();
    }
}
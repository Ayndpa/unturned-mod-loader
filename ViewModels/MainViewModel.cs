using System.Collections.ObjectModel;
using System.Linq;
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

public partial class MainViewModel : ViewModelBase
{
    private readonly SettingsService _settingsService;
    private readonly AppSettings _settings;
    private readonly FolderPickerService _folderPicker;
    private readonly IModsApiClient _modsApi;
    private readonly AuthSessionService _session;
    private readonly Window _owner;
    private readonly Action? _onLogout;
    private readonly Dictionary<int, bool> _enabledStates = [];
    private CancellationTokenSource? _searchDebounceCts;
    private CancellationTokenSource? _loadCts;

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
    public ObservableCollection<CategoryViewModel> Categories { get; } = [];

    public int EnabledCount => Mods.Count(m => m.IsEnabled);
    public int TotalCount => Mods.Count;
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
        Window owner,
        Action? onLogout = null)
    {
        _settingsService = settingsService;
        _settings = settings;
        _folderPicker = folderPicker;
        _modsApi = modsApi;
        _session = session;
        _owner = owner;
        _onLogout = onLogout;
        _gamePath = settings.GamePath;
        _statusText = L.Get(Main.Ready);

        UpdateStatus();
        _ = InitializeAsync();
    }

    [RelayCommand]
    private async Task RefreshModsAsync()
    {
        await LoadCategoriesAsync();
        await LoadModsAsync();
    }

    [RelayCommand]
    private async Task SelectCategoryAsync(string? categoryKey)
    {
        var normalizedKey = string.IsNullOrWhiteSpace(categoryKey) ? null : categoryKey;
        if (normalizedKey == SelectedCategoryKey)
            return;

        SelectedCategoryKey = normalizedKey;
        UpdateCategorySelection();
        await LoadModsAsync();
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
        await LoadCategoriesAsync();
        await LoadModsAsync();

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

    partial void OnGamePathChanged(string value)
    {
        if (GamePathValidator.IsValid(value))
        {
            _settings.GamePath = value;
            _settingsService.Save(_settings);
        }

        UpdateStatus();
    }

    partial void OnSearchTextChanged(string value) => DebounceSearch();

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

            ApplyMods(result.Mods);
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

    private void ApplyMods(IReadOnlyList<RemoteMod> remoteMods)
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
        }

        OnPropertyChanged(nameof(EnabledCount));
        OnPropertyChanged(nameof(TotalCount));
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

    private void UpdateStatus()
    {
        if (IsLoading)
            return;

        if (!string.IsNullOrWhiteSpace(ListMessage) && IsEmpty)
            return;

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
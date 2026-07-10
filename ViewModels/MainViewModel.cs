using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnturnedModLoader.Models;
using UnturnedModLoader.Models.Api;
using UnturnedModLoader.Services;
using UnturnedModLoader.Services.Api;

namespace UnturnedModLoader.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly SettingsService _settingsService;
    private readonly AppSettings _settings;
    private readonly FolderPickerService _folderPicker;
    private readonly IModsApiClient _modsApi;
    private readonly Dictionary<int, bool> _enabledStates = [];
    private CancellationTokenSource? _searchDebounceCts;
    private CancellationTokenSource? _loadCts;

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private string _gamePath = "";

    [ObservableProperty]
    private string _selectedCategory = "全部";

    [ObservableProperty]
    private string _statusText = "就绪";

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

    public bool ShowPackageEmptyIcon => IsEmpty && !IsErrorState && !IsSearchEmpty;
    public bool ShowSearchEmptyIcon => IsEmpty && IsSearchEmpty && !IsErrorState;
    public bool ShowErrorEmptyIcon => IsEmpty && IsErrorState;

    public MainViewModel(
        SettingsService settingsService,
        AppSettings settings,
        FolderPickerService folderPicker,
        IModsApiClient modsApi)
    {
        _settingsService = settingsService;
        _settings = settings;
        _folderPicker = folderPicker;
        _modsApi = modsApi;
        _gamePath = settings.GamePath;

        InitializeCategories();
        UpdateStatus();
        _ = LoadModsAsync();
    }

    [RelayCommand]
    private async Task RefreshModsAsync() => await LoadModsAsync();

    [RelayCommand]
    private async Task SelectCategoryAsync(string? categoryName)
    {
        if (string.IsNullOrWhiteSpace(categoryName) || categoryName == SelectedCategory)
            return;

        SelectedCategory = categoryName;
        UpdateCategorySelection();
        await LoadModsAsync();
    }

    [RelayCommand]
    private async Task BrowseGamePathAsync()
    {
        var picked = await _folderPicker.PickFolderAsync(
            "选择 Unturned 游戏目录",
            string.IsNullOrWhiteSpace(GamePath) ? null : GamePath);

        if (picked is null)
            return;

        GamePath = picked;
        if (GamePathValidator.IsValid(GamePath))
        {
            _settings.GamePath = GamePath;
            _settingsService.Save(_settings);
            StatusText = "游戏路径已更新";
        }
        else
        {
            StatusText = $"无效目录：未找到 {GamePathValidator.ExecutableName}";
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

    private void InitializeCategories()
    {
        Categories.Clear();
        foreach (var label in ModCategoryMapper.AllLabels)
        {
            Categories.Add(new CategoryViewModel(
                label,
                ModCategoryMapper.ToSlug(label),
                ModCategoryMapper.GetIcon(label))
            {
                IsSelected = label == SelectedCategory,
            });
        }
    }

    private void UpdateCategorySelection()
    {
        foreach (var category in Categories)
            category.IsSelected = category.Name == SelectedCategory;
    }

    private async Task LoadModsAsync()
    {
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var token = _loadCts.Token;

        IsLoading = true;
        IsEmpty = false;
        ListMessage = "正在从 API 加载模组…";

        foreach (var mod in Mods)
        {
            if (mod.IsEnabled)
                _enabledStates[mod.Id] = true;
        }

        try
        {
            var query = new ModsQuery
            {
                Category = ModCategoryMapper.ToSlug(SelectedCategory),
                Search = SearchText,
            };

            var result = await _modsApi.GetModsAsync(query, token);
            if (!result.Success)
            {
                Mods.Clear();
                SetEmptyState(
                    isError: true,
                    isSearch: false,
                    title: "无法连接服务端",
                    subtitle: result.Error ?? "请确认本地 API 已启动（默认 http://localhost:3000）");
                StatusText = result.Error ?? "API 请求失败";
                OnPropertyChanged(nameof(EnabledCount));
                OnPropertyChanged(nameof(TotalCount));
                return;
            }

            ApplyMods(result.Mods);
            UpdateCategoryCounts(result.Mods, result.Total);

            if (Mods.Count == 0)
            {
                var hasFilter = !string.IsNullOrWhiteSpace(SearchText) || SelectedCategory != "全部";
                SetEmptyState(
                    isError: false,
                    isSearch: hasFilter,
                    title: hasFilter ? "未找到匹配的模组" : "还没有可用模组",
                    subtitle: hasFilter
                        ? "试试其他关键词或切换分类筛选"
                        : "在网站上传并通过审核后，点击刷新即可在此查看");
                StatusText = $"已连接 {_modsApi.BaseUrl} · 0 个模组";
            }
            else
            {
                IsEmpty = false;
                IsErrorState = false;
                IsSearchEmpty = false;
                ListMessage = "";
                StatusText = $"已加载 {result.Total} 个模组 · {_modsApi.BaseUrl}";
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
                    ? "暂无描述"
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
        if (SelectedCategory == "全部")
        {
            var counts = mods
                .GroupBy(m => ModCategoryMapper.ToLabel(m.Category))
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

            foreach (var category in Categories)
            {
                category.Count = category.Name == "全部"
                    ? apiTotal
                    : counts.TryGetValue(category.Name, out var count) ? count : 0;
            }

            return;
        }

        foreach (var category in Categories)
            category.Count = category.Name == SelectedCategory ? apiTotal : 0;
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

        if (!string.IsNullOrWhiteSpace(ListMessage) &&
            (IsEmpty || StatusText.Contains("无法连接", StringComparison.Ordinal)))
            return;

        StatusText = GamePathValidator.IsValid(GamePath)
            ? $"已连接 {_modsApi.BaseUrl}"
            : string.IsNullOrWhiteSpace(GamePath) ? "游戏路径未配置" : "游戏路径无效";
    }
}
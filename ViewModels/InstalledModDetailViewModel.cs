using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnturnedModLoader.I18n;
using UnturnedModLoader.Services;

namespace UnturnedModLoader.ViewModels;

public partial class InstalledModDetailViewModel : ViewModelBase
{
    private readonly RemoteImageService? _imageService;
    private readonly string? _apiBaseUrl;

    public string Title { get; private set; } = "";
    public string Version { get; private set; } = "";
    public string Category { get; private set; } = "";
    public string Description { get; private set; } = "";
    public int FileCount { get; private set; }

    [ObservableProperty]
    private Bitmap? _coverImage;

    [ObservableProperty]
    private bool _hasCoverImage;

    public event Action? CloseRequested;

    public InstalledModDetailViewModel(RemoteImageService? imageService = null, string? apiBaseUrl = null)
    {
        _imageService = imageService;
        _apiBaseUrl = apiBaseUrl;
    }

    public void Load(InstalledModItemViewModel mod)
    {
        Title = mod.Title;
        Version = mod.Version;
        Category = mod.Category;
        FileCount = mod.FileCount;
        Description = string.IsNullOrWhiteSpace(mod.Description)
            ? L.Get(Common.NoDescription)
            : mod.Description;

        NotifyAll();
        _ = LoadCoverAsync(mod);
    }

    private async Task LoadCoverAsync(InstalledModItemViewModel mod)
    {
        if (_imageService is null || string.IsNullOrWhiteSpace(_apiBaseUrl) || string.IsNullOrWhiteSpace(mod.CoverUrl))
        {
            HasCoverImage = false;
            return;
        }

        var resolved = RemoteImageService.ResolveUrl(_apiBaseUrl, mod.CoverUrl);
        var bitmap = await _imageService.LoadAsync(resolved);
        CoverImage = bitmap;
        HasCoverImage = bitmap is not null;
    }

    private void NotifyAll()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Version));
        OnPropertyChanged(nameof(Category));
        OnPropertyChanged(nameof(FileCount));
        OnPropertyChanged(nameof(Description));
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();
}

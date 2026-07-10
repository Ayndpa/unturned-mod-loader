using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnturnedModLoader.I18n;
using UnturnedModLoader.Models.Api;
using UnturnedModLoader.Services;
using UnturnedModLoader.Services.Api;

namespace UnturnedModLoader.ViewModels;

public partial class ModDetailViewModel : ViewModelBase
{
    private readonly RemoteImageService _imageService;
    private readonly string _apiBaseUrl;

    public int Id { get; private set; }
    public string Title { get; private set; } = "";
    public string Author { get; private set; } = "";
    public string Version { get; private set; } = "";
    public string Category { get; private set; } = "";
    public string Description { get; private set; } = "";
    public string DownloadsText { get; private set; } = "";
    public string LikesText { get; private set; } = "";
    public string CommentsText { get; private set; } = "";
    public string FileSizeText { get; private set; } = "";
    public string TagsText { get; private set; } = "";

    [ObservableProperty]
    private Bitmap? _coverImage;

    [ObservableProperty]
    private bool _hasCoverImage;

    [ObservableProperty]
    private bool _isLoading = true;

    [ObservableProperty]
    private string _errorMessage = "";

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool ShowContent => !IsLoading && !HasError;

    public event Action? CloseRequested;

    public ModDetailViewModel(RemoteImageService imageService, string apiBaseUrl)
    {
        _imageService = imageService;
        _apiBaseUrl = apiBaseUrl;
    }

    public async Task LoadAsync(int modId, IModsApiClient modsApi, CancellationToken cancellationToken = default)
    {
        IsLoading = true;
        ErrorMessage = "";

        try
        {
            var result = await modsApi.GetModAsync(modId, cancellationToken);
            if (!result.Success || result.Mod is null)
            {
                ErrorMessage = result.Error ?? L.Get(ModDetail.LoadFailed);
                return;
            }

            ApplyMod(result.Mod);
            await LoadCoverAsync(result.Mod.CoverUrl, cancellationToken);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ApplyMod(RemoteModDetail mod)
    {
        Id = mod.Id;
        Title = mod.Title;
        Author = mod.AuthorName;
        Version = string.IsNullOrWhiteSpace(mod.Version) ? "—" : mod.Version;
        Category = ModCategoryMapper.ToLabel(mod.Category);
        Description = string.IsNullOrWhiteSpace(mod.Description)
            ? L.Get(Common.NoDescription)
            : mod.Description;
        DownloadsText = mod.Downloads.ToString("N0");
        LikesText = mod.LikeCount.ToString("N0");
        CommentsText = mod.CommentCount.ToString("N0");
        FileSizeText = FormatFileSize(mod.FileSize);
        TagsText = mod.Tags.Count > 0
            ? string.Join(", ", mod.Tags)
            : L.Get(ModDetail.NoTags);

        OnPropertyChanged(nameof(Id));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Author));
        OnPropertyChanged(nameof(Version));
        OnPropertyChanged(nameof(Category));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(DownloadsText));
        OnPropertyChanged(nameof(LikesText));
        OnPropertyChanged(nameof(CommentsText));
        OnPropertyChanged(nameof(FileSizeText));
        OnPropertyChanged(nameof(TagsText));
        OnPropertyChanged(nameof(HasError));
    }

    private async Task LoadCoverAsync(string? coverUrl, CancellationToken cancellationToken)
    {
        var resolved = RemoteImageService.ResolveUrl(_apiBaseUrl, coverUrl);
        var bitmap = await _imageService.LoadAsync(resolved, cancellationToken);
        CoverImage = bitmap;
        HasCoverImage = bitmap is not null;
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes <= 0)
            return "—";

        string[] units = ["B", "KB", "MB", "GB"];
        var size = (double)bytes;
        var unit = 0;

        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:0.##} {units[unit]}";
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(ShowContent));
    partial void OnErrorMessageChanged(string value)
    {
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(ShowContent));
    }
}
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnturnedModLoader.I18n;
using UnturnedModLoader.Models;
using UnturnedModLoader.Models.Api;
using UnturnedModLoader.Services;
using UnturnedModLoader.Services.Api;

namespace UnturnedModLoader.ViewModels;

public partial class ModDetailViewModel : ViewModelBase
{
    private readonly RemoteImageService _imageService;
    private readonly IModsApiClient _modsApi;
    private readonly AppSettings _settings;
    private readonly ModDownloadService _downloadService;
    private readonly Func<string?>? _getInstallModulesFolder;
    private readonly Window _owner;
    private readonly string _apiBaseUrl;
    private int _downloadCount;

    public int Id { get; private set; }
    public string Title { get; private set; } = "";
    public string Author { get; private set; } = "";
    public string Version { get; private set; } = "";
    public string Category { get; private set; } = "";
    public string DescriptionMarkdown { get; private set; } = "";
    public bool HasDescription { get; private set; }
    public bool HasFile { get; private set; }
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
    private bool _isDownloading;

    [ObservableProperty]
    private string _errorMessage = "";

    [ObservableProperty]
    private string _downloadStatus = "";

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool ShowContent => !IsLoading && !HasError;
    public bool CanDownload => ShowContent && HasFile && !IsDownloading;
    public bool HasDownloadStatus => !string.IsNullOrWhiteSpace(DownloadStatus);
    public string DownloadButtonText => IsDownloading
        ? L.Get(ModDetail.Downloading)
        : L.Get(ModDetail.Download);

    public event Action? CloseRequested;
    public event Action? DownloadCompleted;

    public ModDetailViewModel(
        RemoteImageService imageService,
        string apiBaseUrl,
        IModsApiClient modsApi,
        AppSettings settings,
        Window owner,
        ModDownloadService? downloadService = null,
        Func<string?>? getInstallModulesFolder = null)
    {
        _imageService = imageService;
        _apiBaseUrl = apiBaseUrl;
        _modsApi = modsApi;
        _settings = settings;
        _owner = owner;
        _downloadService = downloadService ?? new ModDownloadService();
        _getInstallModulesFolder = getInstallModulesFolder;
    }

    public async Task LoadAsync(int modId, CancellationToken cancellationToken = default)
    {
        IsLoading = true;
        ErrorMessage = "";
        DownloadStatus = "";

        try
        {
            var result = await _modsApi.GetModAsync(modId, cancellationToken);
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
            NotifyDownloadState();
        }
    }

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private async Task DownloadAsync()
    {
        if (!HasFile || IsDownloading)
            return;

        if (!_settings.IsLoggedIn)
        {
            DownloadStatus = L.Get(ModDetail.LoginRequired);
            OnPropertyChanged(nameof(HasDownloadStatus));
            return;
        }

        IsDownloading = true;
        DownloadStatus = "";
        NotifyDownloadState();

        try
        {
            var modulesFolder = _getInstallModulesFolder?.Invoke();
            if (_getInstallModulesFolder is not null && string.IsNullOrWhiteSpace(modulesFolder))
            {
                DownloadStatus = L.Get(ModDetail.VanillaBlocked);
                OnPropertyChanged(nameof(HasDownloadStatus));
                return;
            }

            var result = await _downloadService.DownloadAndInstallAsync(
                _modsApi,
                Id,
                modulesFolder,
                _owner);

            if (result.Cancelled)
            {
                DownloadStatus = "";
            }
            else if (!result.Success)
            {
                DownloadStatus = result.Error ?? L.Get(ModDetail.DownloadFailed);
            }
            else if (result.InstalledToGame)
            {
                DownloadStatus = L.Get(ModDetail.DownloadInstalled);
                _downloadCount++;
                DownloadsText = _downloadCount.ToString("N0");
                OnPropertyChanged(nameof(DownloadsText));
                DownloadCompleted?.Invoke();
            }
            else
            {
                DownloadStatus = L.Get(ModDetail.DownloadSaved, result.Path ?? "");
            }
        }
        finally
        {
            IsDownloading = false;
            NotifyDownloadState();
        }
    }

    private void ApplyMod(RemoteModDetail mod)
    {
        Id = mod.Id;
        Title = mod.Title;
        Author = mod.AuthorName;
        Version = string.IsNullOrWhiteSpace(mod.Version) ? "—" : mod.Version;
        Category = ModCategoryMapper.ToLabel(mod.Category);
        HasDescription = !string.IsNullOrWhiteSpace(mod.Description);
        DescriptionMarkdown = mod.Description ?? "";
        HasFile = mod.HasFile;
        _downloadCount = mod.Downloads;
        DownloadsText = _downloadCount.ToString("N0");
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
        OnPropertyChanged(nameof(DescriptionMarkdown));
        OnPropertyChanged(nameof(HasDescription));
        OnPropertyChanged(nameof(HasFile));
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

    private void NotifyDownloadState()
    {
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(HasDownloadStatus));
        OnPropertyChanged(nameof(DownloadButtonText));
        DownloadCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowContent));
        NotifyDownloadState();
    }

    partial void OnIsDownloadingChanged(bool value)
    {
        OnPropertyChanged(nameof(DownloadButtonText));
        NotifyDownloadState();
    }

    partial void OnErrorMessageChanged(string value)
    {
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(ShowContent));
        NotifyDownloadState();
    }

    protected override void OnLocalizationChanged()
    {
        OnPropertyChanged(nameof(DownloadButtonText));
    }
}
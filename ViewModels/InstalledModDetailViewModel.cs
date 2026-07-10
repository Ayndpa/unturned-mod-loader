using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnturnedModLoader.I18n;
using UnturnedModLoader.Models;
using UnturnedModLoader.Services;

namespace UnturnedModLoader.ViewModels;

public partial class InstalledModDetailViewModel : ViewModelBase
{
    private readonly RemoteImageService? _imageService;
    private readonly string? _apiBaseUrl;

    public string Title { get; private set; } = "";
    public string TypeLabel { get; private set; } = "";
    public string Version { get; private set; } = "";
    public string StatusLabel { get; private set; } = "";
    public string RelativePath { get; private set; } = "";
    public string DirectoryPath { get; private set; } = "";
    public string Description { get; private set; } = "";
    public string DependenciesText { get; private set; } = "";
    public string AssembliesText { get; private set; } = "";

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
        TypeLabel = mod.Kind == LocalModKind.Module
            ? L.Get(InstalledModDetail.TypeModule)
            : L.Get(InstalledModDetail.TypeDll);
        Version = mod.Version;
        StatusLabel = mod.IsEnabled
            ? L.Get(InstalledModDetail.StatusEnabled)
            : L.Get(InstalledModDetail.StatusDisabled);
        RelativePath = mod.RelativePath;
        DirectoryPath = mod.DirectoryPath;
        Description = string.IsNullOrWhiteSpace(mod.Description)
            ? L.Get(Common.NoDescription)
            : mod.Description;
        DependenciesText = mod.Dependencies.Count > 0
            ? string.Join(Environment.NewLine, mod.Dependencies)
            : L.Get(InstalledModDetail.NoDependencies);
        AssembliesText = mod.Assemblies.Count > 0
            ? string.Join(Environment.NewLine, mod.Assemblies)
            : L.Get(InstalledModDetail.NoAssemblies);

        NotifyAll();
        _ = LoadCoverAsync(mod);
    }

    private async Task LoadCoverAsync(InstalledModItemViewModel mod)
    {
        if (!string.IsNullOrWhiteSpace(mod.LocalIconPath) && File.Exists(mod.LocalIconPath))
        {
            try
            {
                await using var stream = File.OpenRead(mod.LocalIconPath);
                CoverImage = new Bitmap(stream);
                HasCoverImage = true;
                return;
            }
            catch
            {
                // Fall through to remote cover.
            }
        }

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
        OnPropertyChanged(nameof(TypeLabel));
        OnPropertyChanged(nameof(Version));
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(RelativePath));
        OnPropertyChanged(nameof(DirectoryPath));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(DependenciesText));
        OnPropertyChanged(nameof(AssembliesText));
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();
}
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace UnturnedModLoader.ViewModels;

public partial class InstalledModItemViewModel : ViewModelBase
{
    public int? RemoteId { get; init; }
    public string Title { get; init; } = "";
    public string Author { get; init; } = "";
    public string Version { get; init; } = "";
    public string Category { get; init; } = "";
    public string Description { get; init; } = "";
    public string FileName { get; init; } = "";

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private Bitmap? _coverImage;

    [ObservableProperty]
    private bool _hasCoverImage;
}
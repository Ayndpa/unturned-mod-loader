using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using UnturnedModLoader.Models;

namespace UnturnedModLoader.ViewModels;

public partial class InstalledModItemViewModel : ViewModelBase
{
    public int? RemoteId { get; init; }
    public LocalModKind Kind { get; init; }
    public string Title { get; init; } = "";
    public string Author { get; init; } = "";
    public string Version { get; init; } = "";
    public string Category { get; init; } = "";
    public string Description { get; init; } = "";
    public string RelativePath { get; init; } = "";
    public string ModuleFilePath { get; init; } = "";
    public string DirectoryPath { get; init; } = "";
    public string? LocalIconPath { get; init; }
    public string? CoverUrl { get; init; }
    public IReadOnlyList<string> Dependencies { get; init; } = [];
    public IReadOnlyList<string> Assemblies { get; init; } = [];

    public string FileName => RelativePath;

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private Bitmap? _coverImage;

    [ObservableProperty]
    private bool _hasCoverImage;
}
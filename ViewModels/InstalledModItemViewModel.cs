using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using UnturnedModLoader.Models;

namespace UnturnedModLoader.ViewModels;

public partial class InstalledModItemViewModel : ViewModelBase
{
    public int? RemoteId { get; init; }
    public LocalModKind Kind { get; init; }
    public string Title { get; init; } = "";
    public string ModuleName { get; init; } = "";
    public string Author { get; init; } = "";
    public string Version { get; init; } = "";
    public string Category { get; init; } = "";
    public string Description { get; init; } = "";
    public string RelativePath { get; init; } = "";
    public string ModuleFilePath { get; init; } = "";
    public string DirectoryPath { get; init; } = "";
    public string? LocalIconPath { get; init; }
    public string? CoverUrl { get; init; }
    public IReadOnlyList<string> DependencyNames { get; init; } = [];
    public IReadOnlyList<string> Dependencies { get; init; } = [];
    public IReadOnlyList<string> Assemblies { get; init; } = [];

    [ObservableProperty]
    private bool _isTogglePending;

    [ObservableProperty]
    private bool _canToggle = true;

    public bool IsToggleAllowed => CanToggle && !IsTogglePending;

    public string FileName => RelativePath;

    partial void OnCanToggleChanged(bool value) => OnPropertyChanged(nameof(IsToggleAllowed));

    partial void OnIsTogglePendingChanged(bool value) => OnPropertyChanged(nameof(IsToggleAllowed));

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private Bitmap? _coverImage;

    [ObservableProperty]
    private bool _hasCoverImage;
}
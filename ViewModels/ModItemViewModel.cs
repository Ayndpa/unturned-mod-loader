using CommunityToolkit.Mvvm.ComponentModel;

namespace UnturnedModLoader.ViewModels;

public partial class ModItemViewModel : ViewModelBase
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public string Author { get; init; } = "";
    public string Version { get; init; } = "";
    public string Category { get; init; } = "";
    public string Description { get; init; } = "";
    public int Downloads { get; init; }
    public int LikeCount { get; init; }

    [ObservableProperty]
    private bool _isEnabled;
}
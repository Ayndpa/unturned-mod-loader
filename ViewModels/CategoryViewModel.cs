using CommunityToolkit.Mvvm.ComponentModel;
using Lucide.Avalonia;

namespace UnturnedModLoader.ViewModels;

public partial class CategoryViewModel : ViewModelBase
{
    public string Name { get; }
    public string? ApiSlug { get; }
    public LucideIconKind IconKind { get; }

    [ObservableProperty]
    private int _count;

    [ObservableProperty]
    private bool _isSelected;

    public string Label => Count > 0 ? $"{Name} ({Count})" : Name;

    public CategoryViewModel(string name, string? apiSlug, LucideIconKind iconKind)
    {
        Name = name;
        ApiSlug = apiSlug;
        IconKind = iconKind;
    }

    partial void OnCountChanged(int value) => OnPropertyChanged(nameof(Label));
}
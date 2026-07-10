using CommunityToolkit.Mvvm.ComponentModel;
using Lucide.Avalonia;
using UnturnedModLoader.I18n;
using UnturnedModLoader.Services;
using UnturnedModLoader.Services.Api;

namespace UnturnedModLoader.ViewModels;

public partial class CategoryViewModel : ViewModelBase
{
    public string? Key { get; }
    public LucideIconKind IconKind { get; }

    [ObservableProperty]
    private int _count;

    [ObservableProperty]
    private bool _isSelected;

    public string Name => ModCategoryMapper.GetDisplayName(Key);
    public string Label => Count > 0 ? L.Get(Category.Count, Name, Count) : Name;

    public CategoryViewModel(string? key, LucideIconKind iconKind)
    {
        Key = key;
        IconKind = iconKind;
    }

    partial void OnCountChanged(int value) => RefreshLabel();

    protected override void OnLocalizationChanged()
    {
        OnPropertyChanged(nameof(Name));
        RefreshLabel();
    }

    private void RefreshLabel() => OnPropertyChanged(nameof(Label));
}
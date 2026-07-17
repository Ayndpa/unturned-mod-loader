using CommunityToolkit.Mvvm.ComponentModel;
using UnturnedModLoader.Models;

namespace UnturnedModLoader.ViewModels;

public partial class ProfileItemViewModel : ObservableObject
{
    public string Id { get; init; } = "";
    public string Name { get; set; } = "";

    [ObservableProperty]
    private bool _isActive;

    public static ProfileItemViewModel From(GameProfile profile, string activeId) => new()
    {
        Id = profile.Id,
        Name = profile.Name,
        IsActive = string.Equals(profile.Id, activeId, StringComparison.OrdinalIgnoreCase),
    };
}

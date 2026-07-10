using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using UnturnedModLoader.Models;

namespace UnturnedModLoader.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private string _gamePath = @"C:\Program Files (x86)\Steam\steamapps\common\Unturned";

    [ObservableProperty]
    private string _selectedCategory = "全部";

    [ObservableProperty]
    private string _statusText = "就绪";

    public ObservableCollection<ModItemViewModel> Mods { get; } = [];
    public ObservableCollection<CategoryViewModel> Categories { get; } = [];

    public int EnabledCount => Mods.Count(m => m.IsEnabled);
    public int TotalCount => Mods.Count;

    public MainViewModel()
    {
        LoadMockData();
    }

    private void LoadMockData()
    {
        var items = new[]
        {
            new ModItem("Tactical Weapons Pack", "ModMaster", "2.1.0", "武器",
                "高质量武器扩展包，包含 40+ 种自定义枪械与配件。", true),
            new ModItem("PEI Expansion", "IslandBuilder", "1.4.2", "地图",
                "扩展 PEI 岛屿地图，新增城镇、地堡与资源点。", false),
            new ModItem("Off-Road Vehicles", "DriveDev", "3.0.1", "载具",
                "越野载具合集：皮卡、ATV、改装吉普等。", true),
            new ModItem("Survival Plus", "HardcoreMod", "1.8.0", "生存",
                "增强生存机制：体温、疾病、高级 crafting 系统。", false),
            new ModItem("Modern UI Overhaul", "PixelCraft", "2.5.0", "界面",
                "现代化 HUD 与菜单界面重制。", true),
            new ModItem("Russia Reborn", "MapForge", "4.2.0", "地图",
                "俄罗斯地图全面重制，新增建筑与任务线。", false),
            new ModItem("Helicopter Pack", "SkyMods", "1.1.3", "载具",
                "可驾驶直升机模组，支持多人联机。", false),
            new ModItem("Zombie Horde", "UndeadLabs", "2.0.0", "其他",
                "强化僵尸 AI 与尸潮事件系统。", false),
        };

        foreach (var item in items)
        {
            var vm = new ModItemViewModel
            {
                Name = item.Name,
                Author = item.Author,
                Version = item.Version,
                Category = item.Category,
                Description = item.Description,
                IsEnabled = item.IsEnabled,
            };
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ModItemViewModel.IsEnabled))
                    OnPropertyChanged(nameof(EnabledCount));
            };
            Mods.Add(vm);
        }

        var categoryCounts = items
            .GroupBy(m => m.Category)
            .ToDictionary(g => g.Key, g => g.Count());

        Categories.Add(new CategoryViewModel("全部", items.Length));
        foreach (var (name, count) in categoryCounts.OrderBy(kv => kv.Key))
            Categories.Add(new CategoryViewModel(name, count));
    }
}
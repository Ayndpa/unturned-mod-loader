using Lucide.Avalonia;
using UnturnedModLoader.Models.Api;

namespace UnturnedModLoader.Services.Api;

public static class ModCategoryMapper
{
    private static readonly Dictionary<string, string> LegacySlugToLabel = new(StringComparer.OrdinalIgnoreCase)
    {
        ["weapon"] = "武器",
        ["map"] = "地图",
        ["vehicle"] = "载具",
        ["survival"] = "生存",
        ["ui"] = "界面",
        ["other"] = "其他",
    };

    private static IReadOnlyList<RemoteCategory> _categories = [];

    public static void SetCategories(IReadOnlyList<RemoteCategory> categories) =>
        _categories = categories;

    public static string ToLabel(string? slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
            return GetDefaultLabel();

        var category = _categories.FirstOrDefault(c =>
            string.Equals(c.Key, slug, StringComparison.OrdinalIgnoreCase));

        if (category is not null)
            return category.NameZh;

        return LegacySlugToLabel.TryGetValue(slug, out var label) ? label : slug;
    }

    public static string? ToSlug(string? label)
    {
        if (string.IsNullOrWhiteSpace(label) || label == "全部")
            return null;

        var category = _categories.FirstOrDefault(c => c.NameZh == label);
        if (category is not null)
            return category.Key;

        var legacy = LegacySlugToLabel.FirstOrDefault(kv => kv.Value == label);
        return string.IsNullOrEmpty(legacy.Key) ? null : legacy.Key;
    }

    public static LucideIconKind GetIconFromApiName(string? iconName) => iconName switch
    {
        "Sword" => LucideIconKind.Sword,
        "Map" => LucideIconKind.Map,
        "Car" => LucideIconKind.Car,
        "Tent" => LucideIconKind.Tent,
        "Monitor" => LucideIconKind.Monitor,
        "Package" => LucideIconKind.Package,
        "Box" => LucideIconKind.Box,
        "Gamepad2" => LucideIconKind.Gamepad2,
        "Shield" => LucideIconKind.Shield,
        "Zap" => LucideIconKind.Zap,
        "Star" => LucideIconKind.Star,
        "Hammer" => LucideIconKind.Hammer,
        _ => LucideIconKind.Folder,
    };

    public static LucideIconKind GetAllCategoryIcon() => LucideIconKind.LayoutGrid;

    private static string GetDefaultLabel()
    {
        var other = _categories.FirstOrDefault(c =>
            string.Equals(c.Key, "other", StringComparison.OrdinalIgnoreCase));

        return other?.NameZh
            ?? _categories.FirstOrDefault()?.NameZh
            ?? LegacySlugToLabel["other"];
    }
}
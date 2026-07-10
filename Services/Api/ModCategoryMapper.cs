using Lucide.Avalonia;
using UnturnedModLoader.I18n;
using UnturnedModLoader.Models.Api;

namespace UnturnedModLoader.Services.Api;

public static class ModCategoryMapper
{
    private static readonly Dictionary<string, string> LegacySlugToI18nKey = new(StringComparer.OrdinalIgnoreCase)
    {
        ["weapon"] = Category.Weapon,
        ["map"] = Category.Map,
        ["vehicle"] = Category.Vehicle,
        ["survival"] = Category.Survival,
        ["ui"] = Category.Ui,
        ["other"] = Category.Other,
    };

    private static IReadOnlyList<RemoteCategory> _categories = [];

    public static void SetCategories(IReadOnlyList<RemoteCategory> categories) =>
        _categories = categories;

    public static string GetDisplayName(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return L.Get(Category.All);

        var category = _categories.FirstOrDefault(c =>
            string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));

        if (category is not null)
            return LocalizationService.CurrentLocaleCode == "zh"
                ? category.NameZh
                : category.NameEn;

        return LegacySlugToI18nKey.TryGetValue(key, out var i18nKey)
            ? L.Get(i18nKey)
            : key;
    }

    public static string ToLabel(string? slug) => GetDisplayName(slug);

    public static string? ToSlug(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
            return null;

        if (label == L.Get(Category.All))
            return null;

        var category = _categories.FirstOrDefault(c =>
            c.NameZh == label || c.NameEn == label);

        if (category is not null)
            return category.Key;

        var legacy = LegacySlugToI18nKey.FirstOrDefault(kv => L.Get(kv.Value) == label);
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

    private static string GetDefaultLabel() => GetDisplayName("other");
}
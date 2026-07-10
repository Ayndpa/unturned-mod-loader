using Lucide.Avalonia;

namespace UnturnedModLoader.Services.Api;

public static class ModCategoryMapper
{
    private static readonly Dictionary<string, string> SlugToLabel = new(StringComparer.OrdinalIgnoreCase)
    {
        ["weapon"] = "武器",
        ["map"] = "地图",
        ["vehicle"] = "载具",
        ["survival"] = "生存",
        ["ui"] = "界面",
        ["other"] = "其他",
    };

    private static readonly Dictionary<string, string> LabelToSlug =
        SlugToLabel.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.Ordinal);

    public static IReadOnlyList<string> AllLabels { get; } =
        ["全部", ..SlugToLabel.Values];

    public static string ToLabel(string? slug) =>
        string.IsNullOrWhiteSpace(slug) ? "其他"
        : SlugToLabel.TryGetValue(slug, out var label) ? label : slug;

    public static string? ToSlug(string? label) =>
        string.IsNullOrWhiteSpace(label) || label == "全部"
            ? null
            : LabelToSlug.TryGetValue(label, out var slug) ? slug : null;

    public static LucideIconKind GetIcon(string label) => label switch
    {
        "全部" => LucideIconKind.LayoutGrid,
        "武器" => LucideIconKind.Crosshair,
        "地图" => LucideIconKind.Map,
        "载具" => LucideIconKind.Car,
        "生存" => LucideIconKind.HeartPulse,
        "界面" => LucideIconKind.LayoutDashboard,
        "其他" => LucideIconKind.Box,
        _ => LucideIconKind.Folder,
    };
}
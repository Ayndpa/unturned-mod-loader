using Avalonia;
using Avalonia.Styling;
using UnturnedModLoader.Models;

namespace UnturnedModLoader.Services;

public static class ThemeService
{
    public static event Action? ThemeChanged;

    public static string CurrentPreference { get; private set; } = "system";

    public static void Initialize(AppSettings settings)
    {
        CurrentPreference = NormalizePreference(settings.Theme);
        ApplyToApplication(CurrentPreference);
    }

    public static void ApplyPreference(string preference, AppSettings settings, SettingsService settingsService)
    {
        var normalized = NormalizePreference(preference);
        if (normalized == CurrentPreference)
            return;

        settings.Theme = normalized;
        settingsService.Save(settings);
        CurrentPreference = normalized;
        ApplyToApplication(normalized);
        ThemeChanged?.Invoke();
    }

    public static string NormalizePreference(string? value) =>
        value is "light" or "dark" or "system" ? value : "system";

    public static ThemeVariant ToVariant(string preference) => preference switch
    {
        "light" => ThemeVariant.Light,
        "dark" => ThemeVariant.Dark,
        _ => ThemeVariant.Default,
    };

    private static void ApplyToApplication(string preference)
    {
        if (Application.Current is null)
            return;

        Application.Current.RequestedThemeVariant = ToVariant(preference);
    }
}
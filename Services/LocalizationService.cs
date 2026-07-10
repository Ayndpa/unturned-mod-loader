using System.Globalization;
using Lang.Avalonia;
using Lang.Avalonia.Json;
using UnturnedModLoader.Models;

namespace UnturnedModLoader.Services;

public static class LocalizationService
{
    public const string ZhCulture = "zh-CN";
    public const string EnCulture = "en-US";

    public static event Action? LanguageChanged;

    public static bool IsInitialized { get; private set; }

    public static string CurrentLocaleCode =>
        I18nManager.Instance.Culture is { TwoLetterISOLanguageName: "zh" } ? "zh" : "en";

    public static void Initialize(AppSettings settings)
    {
        var culture = ResolveCulture(settings.Locale);
        var i18n = I18nManager.Instance;
        i18n.Register(new JsonLangPlugin(), culture, out var error);

        if (!string.IsNullOrWhiteSpace(error))
            throw new InvalidOperationException($"Failed to initialize localization: {error}");

        i18n.Culture = culture;
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
        IsInitialized = true;
    }

    public static void ApplyLocale(string localeCode, AppSettings settings, SettingsService settingsService)
    {
        var culture = ToCultureInfo(localeCode);
        var i18n = I18nManager.Instance;
        if (i18n.Culture?.Name == culture.Name)
            return;

        settings.Locale = localeCode;
        settingsService.Save(settings);

        i18n.Culture = culture;
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
        LanguageChanged?.Invoke();
    }

    public static CultureInfo ResolveCulture(string? storedLocale)
    {
        if (storedLocale is "zh" or "en")
            return ToCultureInfo(storedLocale);

        var system = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return system.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? new CultureInfo(ZhCulture)
            : new CultureInfo(EnCulture);
    }

    public static CultureInfo ToCultureInfo(string localeCode) => localeCode switch
    {
        "zh" => new CultureInfo(ZhCulture),
        "en" => new CultureInfo(EnCulture),
        _ => new CultureInfo(EnCulture),
    };

    public static string DetectDefaultLocaleCode()
    {
        var system = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return system.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh" : "en";
    }
}
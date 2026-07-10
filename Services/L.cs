using Lang.Avalonia;

namespace UnturnedModLoader.Services;

public static class L
{
    public static string Get(string key) =>
        I18nManager.Instance.GetResource(key);

    public static string Get(string key, params object[] args)
    {
        var template = Get(key);
        return args.Length == 0 ? template : string.Format(template, args);
    }
}
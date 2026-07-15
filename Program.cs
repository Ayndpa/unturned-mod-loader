using System;
using Avalonia;
using Velopack;

namespace UnturnedModLoader;

sealed class Program
{
    /// <summary>
    /// Mod id to install when launched from a browser via <c>unmod://install/{id}</c>, or
    /// via the <c>--install {id}</c> CLI flag. Cleared once consumed by the app.
    /// </summary>
    public static int? PendingInstallModId { get; internal set; }

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Must run before any other startup logic (install / update / uninstall hooks).
        VelopackApp.Build().Run();

        PendingInstallModId = ParseInstallIntent(args);

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static int? ParseInstallIntent(string[] args)
    {
        foreach (var arg in args)
        {
            if (string.Equals(arg, "--install", StringComparison.OrdinalIgnoreCase))
                continue;

            if (Services.ProtocolRegistrar.TryParseInstallIntent(arg, out var id))
                return id;
        }

        return null;
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}

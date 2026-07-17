using System;
using Avalonia;
using UnturnedModLoader.Services;
using Velopack;

namespace UnturnedModLoader;

sealed class Program
{
    /// <summary>
    /// Mod id to install when launched from a browser via <c>unmod://install/{id}</c>, or
    /// via the <c>--install {id}</c> CLI flag. Cleared once consumed by the app.
    /// </summary>
    public static int? PendingInstallModId { get; internal set; }

    /// <summary>
    /// Held for the process lifetime so secondary launches can forward args to us.
    /// Disposed on process exit.
    /// </summary>
    internal static SingleInstanceService? SingleInstance { get; private set; }

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Must run before any other startup logic (install / update / uninstall hooks).
        VelopackApp.Build().Run();

        // Single-instance gate: if another process already owns the mutex, forward our args
        // (including unmod://install/{id}) and exit without spinning up a second UI.
        SingleInstance = SingleInstanceService.TryAcquire(args);
        if (SingleInstance is null)
            return;

        PendingInstallModId = ParseInstallIntent(args);

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            SingleInstance.Dispose();
            SingleInstance = null;
        }
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

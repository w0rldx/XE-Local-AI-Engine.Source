namespace XE_Local_AI_Engine.Tray;

using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;

internal static class Program
{
    private const string SingleInstanceName = "XE-Local-AI-Engine.Tray";

    public static bool IsLogMode { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        IsLogMode = args.Any(static arg => string.Equals(arg, "--log", StringComparison.OrdinalIgnoreCase));

        using var singleInstance = TraySingleInstanceLock.TryAcquire(SingleInstanceName);
        if (singleInstance is null)
        {
            return 0;
        }

        if (IsLogMode)
        {
            Trace.TraceInformation("XE Local AI Engine tray started in log mode.");
        }

        return BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
                         .UsePlatformDetect()
                         .LogToTrace();
    }
}

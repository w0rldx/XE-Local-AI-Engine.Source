namespace XE_Local_AI_Engine.Desktop;

using Avalonia;
using Velopack;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                VelopackApp.Build().Run();
            }

            if (DesktopCommandLine.RunsEngine(args))
            {
#pragma warning disable MA0045 // The STA entry point must remain synchronous; this branch never initializes Avalonia.
                return DesktopEngineCommand.RunAsync(args).GetAwaiter().GetResult();
#pragma warning restore MA0045
            }

            var options = DesktopStartupOptions.Parse(args, Environment.GetEnvironmentVariable("XE_DATA_DIR"),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            return AppBuilder.Configure(() => new DesktopApplication(options))
                .UsePlatformDetect()
                .StartWithClassicDesktopLifetime([]);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Desktop could not start ({exception.GetType().Name}). {DesktopText.StartupFailed}");
            return 1;
        }
    }
}

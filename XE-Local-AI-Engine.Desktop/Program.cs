namespace XE_Local_AI_Engine.Desktop;

using Avalonia;
using Velopack;

internal static class Program
{
    /// <summary>On Linux this shell is the Velopack entry point: <c>VelopackApp.Build().Run()</c> stays the first
    ///     statement of a synchronous Main, outside every try, or <c>vpk pack</c>'s verifier rejects the payload.</summary>
    /// <remarks>
    ///     It stays Linux-only because on Windows the entry point is XE-Local-AI-Engine.WindowsLauncher, which already
    ///     ran it. A second call from this child is not a no-op: Velopack 1.2.0's <c>Run</c> deletes superseded
    ///     packages and, when a newer local package exists, calls <c>UpdateExe.Apply</c> and exits, so it would race
    ///     the launcher over the same install.
    /// </remarks>
    [STAThread]
    public static int Main(string[] args)
    {
        if (OperatingSystem.IsLinux())
        {
            VelopackApp.Build().Run();
        }

        try
        {
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
            DesktopStartupDiagnostics.Record($"Desktop could not start ({exception.GetType().Name}). {exception.Message}");
            Console.Error.WriteLine($"Desktop could not start ({exception.GetType().Name}). {DesktopText.StartupFailed}");
            return 1;
        }
    }
}

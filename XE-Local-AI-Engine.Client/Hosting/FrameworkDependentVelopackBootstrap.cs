namespace XE_Local_AI_Engine.Client.Hosting;

using System.Globalization;
using Velopack;
using Velopack.Locators;
using Velopack.Logging;

/// <summary>
///     Starts Velopack with the repository-owned Windows launcher as its process-path authority when the managed host is
///     running through <c>dotnet.exe</c>. Linux and unpackaged/development runs retain Velopack's default locator.
/// </summary>
internal static class FrameworkDependentVelopackBootstrap
{
    internal const string WindowsLauncherFileName = "XE-Local-AI-Engine.WindowsLauncher.exe";

    internal static void Run(string[] args)
    {
        var app = VelopackApp.Build().SetArgs(args);
        if (OperatingSystem.IsWindows())
        {
            var launcherPath = ResolveLauncherPath(isWindows: true,
                Environment.ProcessPath,
                AppContext.BaseDirectory,
                File.Exists);
            if (launcherPath is not null)
            {
                var defaultProcess = new DefaultProcessImpl(NullVelopackLogger.Instance);
                var launcherProcessId = ResolveLauncherProcessId(Environment.GetEnvironmentVariable("XE_WINDOWS_LAUNCHER_PID"),
                    defaultProcess.GetCurrentProcessId());
                app.SetLocator(new WindowsVelopackLocator(new LauncherProcess(defaultProcess, launcherPath, launcherProcessId),
                    customLog: null));
            }
        }

        app.Run();
    }

    internal static string? ResolveLauncherPath(bool isWindows,
        string? processPath,
        string baseDirectory,
        Func<string, bool> fileExists)
    {
        if (!isWindows || string.IsNullOrWhiteSpace(processPath)
                       || !string.Equals(Path.GetFileName(processPath), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var launcherPath = Path.GetFullPath(Path.Combine(baseDirectory, WindowsLauncherFileName));
        return fileExists(launcherPath) ? launcherPath : null;
    }

    internal static uint ResolveLauncherProcessId(string? value, uint managedProcessId) =>
        uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
        && parsed > 0
            ? parsed
            : managedProcessId;

    private sealed class LauncherProcess(IProcessImpl inner, string launcherPath, uint launcherProcessId) : IProcessImpl
    {
        public string GetCurrentProcessPath() =>
            launcherPath;

        public uint GetCurrentProcessId() =>
            launcherProcessId;

        public void StartProcess(string exePath, IEnumerable<string> args, string workDir, bool showWindow) =>
            inner.StartProcess(exePath, args, workDir, showWindow);

        public void Exit(int exitCode) =>
            inner.Exit(exitCode);
    }
}

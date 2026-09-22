namespace XE_Local_AI_Engine.Client.Hosting;

using System.Globalization;
using Velopack;
using Velopack.Locators;
using Velopack.Logging;

/// <summary>Keeps update process identity aligned with the executable supervising the engine.</summary>
internal static class FrameworkDependentVelopackBootstrap
{
    internal const string WindowsLauncherFileName = "XE-Local-AI-Engine.WindowsLauncher.exe";

    /// <summary>The desktop shell publishes its own process id here; the shell duplicates the literal.</summary>
    internal const string SupervisorProcessIdVariable = "XE_DESKTOP_SUPERVISOR_PID";

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

        if (OperatingSystem.IsLinux())
        {
            var supervisor = Environment.GetEnvironmentVariable(SupervisorProcessIdVariable);
            Environment.SetEnvironmentVariable(SupervisorProcessIdVariable, null);
            var process = new DefaultProcessImpl(NullVelopackLogger.Instance);
            app.SetLocator(new LinuxVelopackLocator(CreateSupervisedProcess(process, supervisor), customLog: null));
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

    internal static IProcessImpl CreateSupervisedProcess(IProcessImpl process, string? supervisorId)
    {
        ArgumentNullException.ThrowIfNull(process);
        var processId = process.GetCurrentProcessId();
        var resolved = ResolveLauncherProcessId(supervisorId, processId);
        return resolved == processId ? process : new LauncherProcess(process, process.GetCurrentProcessPath(), resolved);
    }

    private sealed class LauncherProcess : IProcessImpl
    {
        private readonly IProcessImpl _inner;
        private readonly string _launcherPath;
        private readonly uint _launcherProcessId;

        public LauncherProcess(IProcessImpl inner, string launcherPath, uint launcherProcessId)
        {
            _inner = inner;
            _launcherPath = launcherPath;
            _launcherProcessId = launcherProcessId;
        }

        public string GetCurrentProcessPath() =>
            _launcherPath;

        public uint GetCurrentProcessId() =>
            _launcherProcessId;

        public void StartProcess(string exePath, IEnumerable<string> args, string workDir, bool showWindow) =>
            _inner.StartProcess(exePath, args, workDir, showWindow);

        public void Exit(int exitCode) =>
            _inner.Exit(exitCode);
    }
}

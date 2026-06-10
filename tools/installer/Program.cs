namespace XE_Local_AI_Engine.Installer;

using System.Reflection;

using XE_Local_AI_Engine.Installer.Cli;
using XE_Local_AI_Engine.Installer.Driver;
using XE_Local_AI_Engine.Installer.Driver.Windows;
using XE_Local_AI_Engine.Installer.State;
using XE_Local_AI_Engine.Installer.StateMachine;

internal static class Program
{
    private const string DistroName = "xe-engine-runtime";
    private const string BootstrapModel = "qwen3:0.6b";

    // Minimum free disk for the rootfs import + docker load + model pull (plan §7.5 MED-7a). ~12 GB.
    private const long MinimumFreeDiskBytes = 12L * 1024 * 1024 * 1024;

    public static async Task<int> Main(string[] args)
    {
        var parse = ArgumentParser.Parse(args);
        if (!parse.IsSuccess)
        {
            await Console.Error.WriteLineAsync(parse.ErrorMessage).ConfigureAwait(false);
            await Console.Error.WriteLineAsync().ConfigureAwait(false);
            await Console.Error.WriteLineAsync(parse.Usage).ConfigureAwait(false);
            return InstallerExitCode.UsageError;
        }

        var arguments = parse.Arguments!;
        var console = new SystemInstallerConsole();

        if (!OperatingSystem.IsWindows())
        {
            console.WriteError("xe-installer runs on Windows only for RC1.");
            return InstallerExitCode.UnexpectedError;
        }

        try
        {
            var driver = CreateWindowsDriver();
            var stateStore = new FileInstallStateStore(ResolveStateDirectory());
            var context = new InstallContext
            {
                BundlePath = arguments.BundlePath ?? string.Empty,
                InstallerVersion = ResolveInstallerVersion(),
                DistroName = DistroName,
                BootstrapModel = BootstrapModel,
                MinimumFreeDiskBytes = MinimumFreeDiskBytes
            };

            var orchestrator = new InstallerOrchestrator(driver, stateStore, console, context);
            return await orchestrator.RunAsync(arguments).ConfigureAwait(false);
        }
        catch (BundleChecksumException exception)
        {
            console.WriteError(exception.Message);
            return InstallerExitCode.ChecksumMismatch;
        }
        catch (Exception exception) when (exception is InvalidOperationException or FileNotFoundException or IOException)
        {
            console.WriteError(exception.Message);
            return InstallerExitCode.UnexpectedError;
        }
    }

    private static IInstallerEnvironmentDriver CreateWindowsDriver() =>
        new WindowsInstallerDriver(new ProcessRunner(), new WindowsHostConfigWriter(), MinimumFreeDiskBytes);

    private static string ResolveStateDirectory()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(programData, "XE-Local-AI-Engine", "installer");
    }

    private static string ResolveInstallerVersion() =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
        ?? "0.0.0";
}

namespace XE_Local_AI_Engine.Desktop;

using System.Diagnostics;
using System.Runtime.InteropServices;

internal static partial class DesktopEngineCommand
{
    internal static async Task<int> RunAsync(string[] args)
    {
        var options = DesktopStartupOptions.Parse([], Environment.GetEnvironmentVariable("XE_DATA_DIR"),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        var start = DesktopEngineSession.CreateStartInfo(options.DataDirectory);
        start.RedirectStandardOutput = false;
        start.RedirectStandardError = false;
        start.CreateNoWindow = false;
        foreach (var argument in DesktopCommandLine.EngineArguments(args))
        {
            start.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = start };
        if (!process.Start())
        {
            throw new InvalidOperationException("The engine could not start.");
        }

        using var stopping = new CancellationTokenSource();
        using var terminate = OperatingSystem.IsLinux() ? RegisterSignal(PosixSignal.SIGTERM, stopping) : null;
        using var interrupt = OperatingSystem.IsLinux() ? RegisterSignal(PosixSignal.SIGINT, stopping) : null;
        try
        {
            await process.WaitForExitAsync(stopping.Token);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            await StopOwnedAsync(process);
        }

        return process.ExitCode;
    }

    private static PosixSignalRegistration RegisterSignal(PosixSignal signal, CancellationTokenSource stopping) =>
        PosixSignalRegistration.Create(signal, context =>
        {
            context.Cancel = true;
#pragma warning disable MA0045 // PosixSignalRegistration requires a synchronous callback; publish cancellation before returning to the signal handler.
            stopping.Cancel();
#pragma warning restore MA0045
        });

    private static async Task StopOwnedAsync(Process process)
    {
        if (!process.HasExited)
        {
            _ = SendSignal(process.Id, 15);
        }

        try
        {
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(40), CancellationToken.None);
        }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int SendSignal(int processId, int signal);
}

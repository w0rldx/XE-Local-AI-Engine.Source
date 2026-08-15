namespace XE_Local_AI_Engine.Providers.Training.Implementation;

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Channels;
using XE_Local_AI_Engine.Providers.Training.Contracts;

/// <summary>
///     A running trainer. Signals target the process GROUP so the dataloader workers and compile subprocesses the
///     trainer forked die with it; <see cref="RequestStop" /> stops at SIGTERM because <c>train.py</c> handles it
///     cooperatively and a SIGKILL there would turn every operator cancel into a failure.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed partial class LinuxTrainingProcessHandle(
    Process process,
    TrainingLaunchReceipt receipt,
    Channel<string> output) : ITrainingProcessHandle
{
    private const int Sigterm = 15;
    private const int Sigkill = 9;

    private int _disposed;

    public TrainingLaunchReceipt Receipt { get; } = receipt;

    public IAsyncEnumerable<string> ReadOutputAsync(CancellationToken cancellationToken) =>
        output.Reader.ReadAllAsync(cancellationToken);

    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
    {
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return process.ExitCode;
    }

    public void RequestStop()
    {
        if (HasExited())
        {
            return;
        }

        _ = Kill(-Receipt.Pgid, Sigterm);
    }

    public void KillGroup()
    {
        if (HasExited())
        {
            return;
        }

        _ = Kill(-Receipt.Pgid, Sigterm);
        if (!process.WaitForExit(2000))
        {
            _ = Kill(-Receipt.Pgid, Sigkill);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, value: 1) != 0)
        {
            return;
        }

        try
        {
            KillGroup();
        }
        finally
        {
            _ = output.Writer.TryComplete();
            process.Dispose();
        }
    }

    private bool HasExited()
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true; // No associated process — treat as exited.
        }
    }

    // int kill(pid_t pid, int sig); — a negative pid signals the process group abs(pid).
    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int Kill(int pid, int sig);
}

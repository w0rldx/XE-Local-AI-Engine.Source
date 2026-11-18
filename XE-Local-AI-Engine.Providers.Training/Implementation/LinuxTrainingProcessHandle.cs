namespace XE_Local_AI_Engine.Providers.Training.Implementation;

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.Training.Contracts;

/// <summary>
///     A running trainer. Signals target the process GROUP, so the dataloader workers and compile subprocesses the
///     trainer forked die with it.
/// </summary>
/// <remarks>
///     <see cref="RequestStop" /> stops at SIGTERM because <c>train.py</c> handles it cooperatively, and a SIGKILL
///     there would turn every operator cancel into a failure.
/// </remarks>
[SupportedOSPlatform("linux")]
internal sealed partial class LinuxTrainingProcessHandle : ITrainingProcessHandle
{
    private const int Sigterm = 15;
    private const int Sigkill = 9;
    private static readonly TimeSpan ExitPollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan KillGrace = TimeSpan.FromSeconds(2);
    private readonly Func<int, int, int> _kill;
    private readonly ILogger _logger;
    private readonly Process _process;
    private readonly Channel<string> _output;
    private readonly SemaphoreSlim _stdinGate = new(1, 1);
    private readonly TimeProvider _timeProvider;
    private readonly Func<TimeSpan, bool> _waitForExit;

    private int _disposed;

    public LinuxTrainingProcessHandle(Process process,
        TrainingLaunchReceipt receipt,
        Channel<string> output,
        ILogger logger,
        TimeProvider timeProvider)
        : this(process, receipt, output, logger, timeProvider, Kill, process.WaitForExit)
    {
    }

    /// <param name="kill"><c>kill(2)</c>.</param>
    /// <param name="waitForExit">The wait between the SIGTERM and the SIGKILL in <see cref="KillGroup" />; true once the trainer exited.</param>
    internal LinuxTrainingProcessHandle(Process process,
        TrainingLaunchReceipt receipt,
        Channel<string> output,
        ILogger logger,
        TimeProvider timeProvider,
        Func<int, int, int> kill,
        Func<TimeSpan, bool> waitForExit)
    {
        _logger = logger;
        _process = process;
        Receipt = receipt;
        _output = output;
        _timeProvider = timeProvider;
        _kill = kill;
        _waitForExit = waitForExit;
    }

    public TrainingLaunchReceipt Receipt { get; }

    public IAsyncEnumerable<string> ReadOutputAsync(CancellationToken cancellationToken) =>
        _output.Reader.ReadAllAsync(cancellationToken);

    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
    {
        await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return _process.ExitCode;
    }

    public async Task<int?> WaitForGroupExitAsync(CancellationToken cancellationToken)
    {
        // A pgid is not reused while any member lives, and a reuse of an empty one only reads as non-empty: both fail closed.
        var hostGroup = LinuxTrainingProcessInspector.HostProcessGroupId;
        try
        {
            if (!TrainingProcessGroupGuard.MaySignalGroup(Receipt.Pid, Receipt.Pgid, hostGroup))
            {
                // No group to probe: the pipe EOF is the only sign the trainer's children are gone, so keep waiting for it.
                var exitCode = await WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                // A receipt read before setsid ran holds the host's group, yet the trainer may lead one named by its pid; a child that closed its pipes stays there.
                while (Receipt.Pid > 1 && Receipt.Pid != hostGroup && GroupHasMembers(Receipt.Pid))
                {
                    await Task.Delay(ExitPollInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
                }

                return exitCode;
            }

            // HasExited reads the wait status only; Process.WaitForExitAsync would also wait for pipes a descendant may hold open.
            while (!HasExited() || GroupHasMembers(Receipt.Pgid))
            {
                await Task.Delay(ExitPollInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        try
        {
            return _process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return -1; // Disposed: HasExited already reads it as exited, and its code is gone.
        }
    }

    public async Task WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.AsSpan().ContainsAny('\n', '\r'))
        {
            throw new ArgumentException("A stdin command is one line; an embedded line break would split it into two.", nameof(line));
        }

        if (!_process.StartInfo.RedirectStandardInput)
        {
            throw new InvalidOperationException("This trainer was spawned without a stdin pipe.");
        }

        // The raw stream, not StandardInput's writer, so the encoding is UTF-8 without a BOM whatever the platform default.
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        await _stdinGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stdin = _process.StandardInput.BaseStream;
            await stdin.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = _stdinGate.Release();
        }
    }

    public void RequestStop()
    {
        if (HasExited())
        {
            return;
        }

        SignalIfSameProcess(SignalTarget(), Sigterm);
    }

    public void KillGroup()
    {
        if (HasExited())
        {
            return;
        }

        var target = SignalTarget();
        SignalIfSameProcess(target, Sigterm);
        if (!_waitForExit(KillGrace))
        {
            SignalIfSameProcess(target, Sigkill);
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
            _ = _output.Writer.TryComplete();
            _process.Dispose();
            _stdinGate.Dispose();
        }
    }

    /// <summary>The negated group when the receipt proves the trainer leads a group of its own, else the bare pid.</summary>
    private int SignalTarget()
    {
        if (TrainingProcessGroupGuard.MaySignalGroup(Receipt.Pid, Receipt.Pgid, LinuxTrainingProcessInspector.HostProcessGroupId))
        {
            return -Receipt.Pgid;
        }

        _logger.LogWarning("Refusing to signal process group {Pgid} for trainer pid {Pid}; signalling the pid only.", Receipt.Pgid, Receipt.Pid);
        return Receipt.Pid;
    }

    /// <summary>Sends <paramref name="signal" /> only while the trainer pid still has the receipt's start time; with none recorded, to the bare pid while it lives.</summary>
    private void SignalIfSameProcess(int target, int signal)
    {
        if (Receipt.StartTicks <= 0)
        {
            // The runtime reaps on SIGCHLD, so a pid it saw exit may name a stranger. Check-then-kill leaves the window Process.Kill
            // has; pidfd_open + pidfd_send_signal is the upgrade that closes it.
            if (!HasExited())
            {
                _ = _kill(Receipt.Pid, signal);
                _logger.LogWarning("Trainer pid {Pid} has no recorded start time; signal {Signal} was sent to the pid only, unverified.", Receipt.Pid, signal);
            }

            return;
        }

        if (!LinuxTrainingProcessInspector.MaySignal(LinuxTrainingProcessInspector.ReadStat(Receipt.Pid), Receipt.StartTicks))
        {
            _logger.LogWarning("Trainer pid {Pid} no longer carries its recorded start time; signal {Signal} was not sent.", Receipt.Pid, signal);
            return;
        }

        _ = _kill(target, signal);
    }

    private bool GroupHasMembers(int processGroupId) =>
        LinuxTrainingProcessInspector.GroupHasMembers(_kill, processGroupId);

    private bool HasExited()
    {
        try
        {
            return _process.HasExited;
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

namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;

using System.Text;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Mxc;
using XE_Local_AI_Engine.Providers.ProcessSupervision;

/// <summary>
///     The <see cref="ISandboxChildProcess" /> over a plain Windows command inside its own Job Object (ADR 0025): one job per command, so
///     whatever the command started dies with it.
/// </summary>
/// <remarks>
///     When the root exits, the job is terminated first, which ends every member still holding an output pipe, and the line pumps then
///     get <see cref="MxcChildProcess.PumpDrainBudget" /> to reach end of stream. Past it (a process outside the job inherited a pipe end,
///     the residual the launcher's handle rules leave) the pending reads are cancelled and the wait returns. Output is read as UTF-8,
///     as on the MXC path. <see cref="Kill" /> terminates the job; <see cref="Dispose" /> closes it.
/// </remarks>
internal sealed class WindowsJobSandboxChildProcess : ISandboxChildProcess
{
    private readonly WindowsJobProcess _process;
    private readonly Task[] _pumps;
    private readonly bool _standardOutputPumped;
    private readonly TimeProvider _timeProvider;

    public WindowsJobSandboxChildProcess(WindowsJobProcess process,
        TimeProvider timeProvider,
        Action<string>? onStandardOutputLine,
        Action<string> onStandardErrorLine)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(onStandardErrorLine);
        _process = process;
        _timeProvider = timeProvider;
        var pumps = new List<Task>(2) { MxcChildProcess.PumpLinesAsync(process.StandardError, onStandardErrorLine) };
        if (onStandardOutputLine is not null)
        {
            pumps.Add(MxcChildProcess.PumpLinesAsync(process.StandardOutput, onStandardOutputLine));
            _standardOutputPumped = true;
        }

        _pumps = [.. pumps];
    }

    public int Id => _process.Id;

    public Stream StandardInput => _process.StandardInput;

    public Stream StandardOutput => _standardOutputPumped
        ? throw new InvalidOperationException("The job child's stdout is pumped to a line callback.")
        : _process.StandardOutput;

    public bool TimedOut => false;

    public int? ExitCode => _process.ExitCode;

    public IReadOnlyList<string> Warnings => [];

    public async Task WriteStandardInputAndCloseAsync(string text, CancellationToken cancellationToken)
    {
        await using var stream = _process.StandardInput;
        await stream.WriteAsync(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text), cancellationToken);
    }

    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
    {
        await _process.WaitForExitAsync(cancellationToken);
        var exitCode = _process.ExitCode ?? -1;
        // The command is over: its members die with it, which closes the pipes they hold.
        _process.Terminate();
        await DrainPumpsAsync();
        return exitCode;
    }

    public async Task<bool> TryWaitForExitAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(_pumps).WaitAsync(cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    public void Kill() =>
        _process.Terminate();

    public void Dispose() =>
        _process.Dispose();

    private async Task DrainPumpsAsync()
    {
        var drained = Task.WhenAll(_pumps);
        using var budget = new CancellationTokenSource();
        if (await Task.WhenAny(drained, Task.Delay(MxcChildProcess.PumpDrainBudget, _timeProvider, budget.Token)) == drained)
        {
            await budget.CancelAsync();
            await drained;
            return;
        }

        // The pumps end on the cancelled read (an I/O error they swallow); nothing awaits them past this point.
        _process.CancelPendingReads();
    }
}

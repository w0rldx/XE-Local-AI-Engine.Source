namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;

using System.Text;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Mxc;
using XE_Local_AI_Engine.Providers.ProcessSupervision;

/// <summary>
///     The <see cref="ISandboxChildProcess" /> over a plain Windows command inside its own Job Object (ADR 0025): one job per command, so
///     whatever the command started dies with it.
/// </summary>
/// <remarks>
///     When the root exits, the job is terminated first, ending every member that holds an output pipe, and the pumps get
///     <see cref="MxcChildProcess.PumpDrainBudget" /> to reach end of stream; past it (a process outside the job holds a pipe end) their
///     overlapped reads are cancelled, the pumps deliver what they hold and the wait returns. Text is UTF-8 both ways, no preamble, as on the MXC path: git, Python and
///     dotnet output is the data that matters (patches are stored as UTF-8), so a cmd builtin printing in the OEM code page is not
///     decoded faithfully, an accepted trade-off (ADR 0025).
/// </remarks>
internal sealed class WindowsJobSandboxChildProcess : ISandboxChildProcess
{
    private static readonly UTF8Encoding Utf8WithoutPreamble = new(encoderShouldEmitUTF8Identifier: false);

    private readonly WindowsJobProcess _process;
    private readonly Task<bool>[] _pumps;
    private readonly CancellationTokenSource _readsCancelled = new();
    private readonly Task<bool> _standardErrorPump;
    private readonly Task<bool>? _standardOutputPump;
    private bool _standardErrorCut;
    private bool _standardOutputCut;
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
        _standardErrorPump = Pump(process.StandardError, onStandardErrorLine);
        if (onStandardOutputLine is null)
        {
            _pumps = [_standardErrorPump];
        }
        else
        {
            _standardOutputPump = Pump(process.StandardOutput, onStandardOutputLine);
            _pumps = [_standardErrorPump, _standardOutputPump];
        }
    }

    public int Id => _process.Id;

    public Stream StandardInput => _process.StandardInput;

    public Stream StandardOutput => _standardOutputPump is not null
        ? throw new InvalidOperationException("The job child's stdout is pumped to a line callback.")
        : _process.StandardOutput;

    public bool TimedOut => false;

    public int? ExitCode => _process.ExitCode;

    public bool StandardOutputIncomplete => _standardOutputCut || _standardOutputPump is { IsCompletedSuccessfully: false } or { Result: true };

    public bool StandardErrorIncomplete => _standardErrorCut || _standardErrorPump is { IsCompletedSuccessfully: false } or { Result: true };

    public IReadOnlyList<string> Warnings => [];

    public async Task WriteStandardInputAndCloseAsync(string text, CancellationToken cancellationToken)
    {
        await using var stream = _process.StandardInput;
        await stream.WriteAsync(Utf8WithoutPreamble.GetBytes(text), cancellationToken);
    }

    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
    {
        await _process.WaitForExitAsync(cancellationToken);
        var exitCode = _process.ExitCode ?? -1;
        // The command is over: its members die with it, which closes the pipes they hold.
        _process.Terminate();
        await DrainPumpsAsync(cancellationToken);
        return exitCode;
    }

    public async Task<bool> TryWaitForExitAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _process.WaitForExitAsync(cancellationToken);
            // As in WaitForExitAsync: once the root is gone, no member may keep the pumps open.
            _process.Terminate();
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

    public void Dispose()
    {
        // Ends the pumps before the token's source goes away, so no read registers on a disposed source.
        _readsCancelled.Cancel();
        _readsCancelled.Dispose();
        _process.Dispose();
    }

    private Task<bool> Pump(Stream stream, Action<string> onLine) =>
        MxcChildProcess.PumpLinesAsync(stream, onLine, _readsCancelled.Token);

    /// <summary>
    ///     Gives the pumps <see cref="MxcChildProcess.PumpDrainBudget" />, ended early by <paramref name="cancellationToken" />, then cuts
    ///     what is still open; a cancel then throws, so a stop or timeout during the drain is never reported as a completed command.
    /// </summary>
    private async Task DrainPumpsAsync(CancellationToken cancellationToken)
    {
        var drained = Task.WhenAll(_pumps);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (await Task.WhenAny(drained, Task.Delay(MxcChildProcess.PumpDrainBudget, _timeProvider, budget.Token)) == drained)
        {
            await budget.CancelAsync();
            await drained;
            return;
        }

        // A process outside the job holds a pipe: cancelling the overlapped reads ends the pumps at once, and each delivers its pending
        // partial line before the caller reads the capture.
        _standardOutputCut |= _standardOutputPump is { IsCompleted: false };
        _standardErrorCut |= _standardErrorPump is { IsCompleted: false };
        await _readsCancelled.CancelAsync();
        await MxcChildProcess.AwaitClosedPumpsAsync(drained, _timeProvider);
        cancellationToken.ThrowIfCancellationRequested();
    }
}

namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;

using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Mxc;
using XE_Local_AI_Engine.Providers.ProcessSupervision;

/// <summary>
///     The <see cref="ISandboxChildProcess" /> over a plain Windows command inside its own Job Object (ADR 0025): one job per command, so
///     whatever the command started dies with it.
/// </summary>
/// <remarks>
///     When the root exits, the job is terminated first, ending every member that holds an output pipe, and the pumps get
///     <see cref="MxcChildProcess.PumpDrainBudget" /> to reach end of stream; past it (a process outside the job holds a pipe end) their
///     overlapped reads are cancelled and the wait returns. Text is coded as .NET's <c>Process</c> codes it by default on Windows: the
///     console code pages, through the same <c>EncodingHelper</c> behind <see cref="Console.OutputEncoding" /> and
///     <see cref="Console.InputEncoding" />, with byte-order-mark detection on read and no preamble on write.
/// </remarks>
internal sealed class WindowsJobSandboxChildProcess : ISandboxChildProcess
{
    private readonly WindowsJobProcess _process;
    private readonly Task[] _pumps;
    private readonly CancellationTokenSource _readsCancelled = new();
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
        var pumps = new List<Task>(2) { Pump(process.StandardError, onStandardErrorLine) };
        if (onStandardOutputLine is not null)
        {
            pumps.Add(Pump(process.StandardOutput, onStandardOutputLine));
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
        await stream.WriteAsync(Console.InputEncoding.GetBytes(text), cancellationToken);
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

    private Task Pump(Stream stream, Action<string> onLine) =>
        MxcChildProcess.PumpLinesAsync(stream, onLine, Console.OutputEncoding, detectEncodingFromByteOrderMarks: true, _readsCancelled.Token);

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

        // A process outside the job holds a pipe: cancel the pumps' reads, which ends them; nothing awaits them past this point.
        await _readsCancelled.CancelAsync();
    }
}

namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Mxc;

using System.Text;
using Microsoft.Mxc.Sdk.V1;

/// <summary>
///     A <see cref="System.Diagnostics.Process" />-shaped adapter over a spawned MXC child: stdio, a wait that kills on cancellation or
///     timeout, and line pumps for the output streams the caller does not read itself.
/// </summary>
/// <remarks>
///     MXC's cancelled wait leaves the child running and its timeout fires only inside a wait, so <see cref="WaitForExitAsync" /> kills
///     the tree and waits again without a token. A stream given a line callback is pumped from construction so the child never blocks
///     on a full pipe, and its property is then <see langword="null" />; a stream without one is exposed raw for the caller to read.
///     After exit or kill the pumps get <see cref="PumpDrainBudget" /> to reach end of stream: a grandchild that inherited the pipe can
///     hold it open forever, so past the budget the pumped streams are closed through the SDK's closers and the wait returns.
/// </remarks>
public sealed class MxcChildProcess : IDisposable
{
    /// <summary>How long a pumped stream may stay open after the child exited or was killed before it is closed under the pump.</summary>
    internal static readonly TimeSpan PumpDrainBudget = TimeSpan.FromSeconds(5);

    private readonly List<IMxcStreamCloser> _closers = new(2);
    private readonly IMxcProcess _process;
    private readonly Task[] _pumps;
    private readonly TimeProvider _timeProvider;

    public MxcChildProcess(IMxcProcess process,
        TimeProvider timeProvider,
        Action<string>? onStandardOutputLine = null,
        Action<string>? onStandardErrorLine = null)
    {
        _process = process ?? throw new ArgumentNullException(nameof(process));
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
        var pumps = new List<Task>(2);
        if (onStandardOutputLine is not null && process.StandardOutput is { } standardOutput)
        {
            // The SDK hands out a closer only for a stream already taken, so it is requested right after the stream.
            pumps.Add(PumpLinesAsync(standardOutput, onStandardOutputLine));
            AddCloser(process.StandardOutputCloser);
        }
        else if (process.StandardOutput is { } rawOutput)
        {
            // Raw stdout's SDK read ignores its token; the wrapper closes the stream on cancellation so a reader's teardown is not held.
            StandardOutput = new MxcCancellableStdoutStream(rawOutput, process.StandardOutputCloser);
        }

        if (onStandardErrorLine is not null && process.StandardError is { } standardError)
        {
            pumps.Add(PumpLinesAsync(standardError, onStandardErrorLine));
            AddCloser(process.StandardErrorCloser);
        }
        else
        {
            StandardError = process.StandardError;
        }

        _pumps = [.. pumps];
    }

    /// <summary>The OS process id of the contained child.</summary>
    public int Id => checked((int)_process.Id);

    /// <summary>Writable stdin, or <see langword="null" /> when not piped; disposing it sends EOF.</summary>
    public Stream? StandardInput => _process.StandardInput;

    /// <summary>Raw stdout as a <see cref="MxcCancellableStdoutStream" />, or <see langword="null" /> when it is pumped or not piped.</summary>
    public Stream? StandardOutput { get; }

    /// <summary>Raw stderr, or <see langword="null" /> when it is pumped to a line callback or not piped.</summary>
    public Stream? StandardError { get; }

    /// <summary>MXC enforced the request's own timeout on the last completed wait.</summary>
    public bool TimedOut { get; private set; }

    /// <summary>MXC's warnings for this run, including cleanup steps that failed after exit.</summary>
    public IReadOnlyList<string> Warnings => _process.Warnings;

    /// <summary>The exit code once the child has exited, else <see langword="null" />. Never waits.</summary>
    public int? ExitCode => _process.TryGetExitCode(out var exitCode) ? exitCode : null;

    /// <summary>
    ///     Waits for exit and for every pumped stream to reach end of stream, at most <see cref="PumpDrainBudget" /> after exit; returns
    ///     the exit code.
    /// </summary>
    /// <exception cref="OperationCanceledException">
    ///     <paramref name="cancellationToken" /> fired; the child was killed and reaped before this is thrown.
    /// </exception>
    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
    {
        WaitResult result;
        try
        {
            result = await _process.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await KillAndReapAsync();
            throw;
        }

        if (result.TimedOut)
        {
            TimedOut = true;
            result = await KillAndReapAsync() ?? result;
        }

        await DrainPumpsAsync();
        return result.ExitCode;
    }

    /// <summary>
    ///     Waits for exit and the pumped streams WITHOUT killing on cancellation: MXC abandons a cancelled wait and leaves the child
    ///     running. Returns <see langword="false" /> when <paramref name="cancellationToken" /> fired first.
    /// </summary>
    public async Task<bool> TryWaitForExitAsync(CancellationToken cancellationToken)
    {
        try
        {
            _ = await _process.WaitAsync(cancellationToken);
            await Task.WhenAll(_pumps).WaitAsync(cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>Kills the whole contained process tree.</summary>
    public void Kill() =>
        _process.Kill();

    /// <summary>Kills a still-running child and frees its handles (MXC semantics).</summary>
    public void Dispose() =>
        _process.Dispose();

    private async Task<WaitResult?> KillAndReapAsync()
    {
        if (_process.TryGetExitCode(out _))
        {
            return null;
        }

        _process.Kill();
        var reaped = await _process.WaitAsync(CancellationToken.None);
        await DrainPumpsAsync();
        return reaped;
    }

    private void AddCloser(IMxcStreamCloser? closer)
    {
        if (closer is not null)
        {
            _closers.Add(closer);
        }
    }

    /// <summary>The child is gone; gives the pumps <see cref="PumpDrainBudget" />, then closes what is still open and returns.</summary>
    private async Task DrainPumpsAsync()
    {
        var drained = Task.WhenAll(_pumps);
        using var budget = new CancellationTokenSource();
        if (await Task.WhenAny(drained, Task.Delay(PumpDrainBudget, _timeProvider, budget.Token)) == drained)
        {
            // Stop the budget timer the moment the pumps finish; the abandoned delay ends cancelled and nothing observes it.
            await budget.CancelAsync();
            await drained;
            return;
        }

        // The pumps end on the closed pipe (end of stream or an I/O error they swallow); nothing awaits them past this point.
        foreach (var closer in _closers)
        {
            closer.Close();
        }
    }

    private static async Task PumpLinesAsync(Stream stream, Action<string> onLine)
    {
        // Yield first so a synchronously completing stream cannot run the whole pump inside the constructor.
        await Task.Yield();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
        try
        {
            while (await reader.ReadLineAsync(CancellationToken.None) is { } line)
            {
                onLine(line);
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // The drain budget ran out and the stream was closed under this read; the lines read so far were delivered.
        }
    }
}

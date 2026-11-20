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

    /// <summary>The longest line a pump holds: the capture budget, since the capture would cut a longer line anyway.</summary>
    internal const int MaxPendingLineChars = ProcessSandboxRuntimeProvider.DefaultMaxCapturedOutputBytes;

    private readonly List<IMxcStreamCloser> _closers = new(2);
    private readonly IMxcProcess _process;
    private readonly Task<bool>[] _pumps;
    private readonly Task<bool>? _standardErrorPump;
    private readonly Task<bool>? _standardOutputPump;
    private readonly TimeProvider _timeProvider;

    public MxcChildProcess(IMxcProcess process,
        TimeProvider timeProvider,
        Action<string>? onStandardOutputLine = null,
        Action<string>? onStandardErrorLine = null)
    {
        ArgumentNullException.ThrowIfNull(process);
        _process = process;
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
        if (onStandardOutputLine is not null && process.StandardOutput is { } standardOutput)
        {
            // The SDK hands out a closer only for a stream already taken, so it is requested right after the stream.
            _standardOutputPump = PumpLinesAsync(standardOutput, onStandardOutputLine, CancellationToken.None);
            AddCloser(process.StandardOutputCloser);
        }
        else if (process.StandardOutput is { } rawOutput)
        {
            // Raw stdout's SDK read ignores its token; the wrapper closes the stream on cancellation so a reader's teardown is not held.
            StandardOutput = new MxcCancellableStdoutStream(rawOutput, process.StandardOutputCloser);
        }

        if (onStandardErrorLine is not null && process.StandardError is { } standardError)
        {
            _standardErrorPump = PumpLinesAsync(standardError, onStandardErrorLine, CancellationToken.None);
            AddCloser(process.StandardErrorCloser);
        }
        else
        {
            StandardError = process.StandardError;
        }

        _pumps = [.. new[] { _standardOutputPump, _standardErrorPump }.OfType<Task<bool>>()];
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

    /// <summary>The stdout pump finished and reported its lines incomplete (<see cref="PumpLinesAsync" />).</summary>
    public bool StandardOutputIncomplete => _standardOutputPump is { IsCompletedSuccessfully: true, Result: true };

    /// <summary>As <see cref="StandardOutputIncomplete" />, for stderr.</summary>
    public bool StandardErrorIncomplete => _standardErrorPump is { IsCompletedSuccessfully: true, Result: true };

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

        // Closing ends the native read, so the pumps end on the closed pipe (end of stream or an I/O error they swallow) and deliver a
        // pending partial line before the caller reads the capture.
        foreach (var closer in _closers)
        {
            closer.Close();
        }

        // A pumped stream the SDK gave no closer for cannot be ended from here, so then nothing waits for the pumps.
        if (_closers.Count == _pumps.Length)
        {
            await drained;
        }
    }

    /// <summary>
    ///     Delivers each UTF-8 line of <paramref name="stream" /> (split as <see cref="StreamReader.ReadLine" /> splits) until end of stream,
    ///     an I/O error or <paramref name="cancellationToken" />; returns <see langword="true" /> when what it delivered is incomplete.
    /// </summary>
    /// <remarks>
    ///     A line is held at most <see cref="MaxPendingLineChars" />: past it the line is delivered cut and the rest up to its newline is
    ///     read and discarded, so a command printing without newlines cannot grow the engine. A final unterminated line is delivered at
    ///     end of stream, and also when the read stops early, which then counts as incomplete because its line may have gone on.
    /// </remarks>
    internal static async Task<bool> PumpLinesAsync(Stream stream, Action<string> onLine, CancellationToken cancellationToken)
    {
        // Yield first so a synchronously completing stream cannot run the whole pump inside the constructor.
        await Task.Yield();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
        var buffer = new char[4096];
        var line = new StringBuilder();
        var discarding = false;
        var afterCarriageReturn = false;
        var incomplete = false;
        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer, cancellationToken)) > 0)
            {
                var chunk = buffer.AsSpan(0, read);
                // A "\r\n" split across two reads is one terminator.
                if (afterCarriageReturn && chunk[0] == '\n')
                {
                    chunk = chunk[1..];
                }

                afterCarriageReturn = false;
                while (!chunk.IsEmpty)
                {
                    var end = chunk.IndexOfAny('\r', '\n');
                    var text = end < 0 ? chunk : chunk[..end];
                    if (!discarding)
                    {
                        var room = MaxPendingLineChars - line.Length;
                        if (text.Length <= room)
                        {
                            line.Append(text);
                        }
                        else
                        {
                            // Never end a cut line on half a surrogate pair.
                            var kept = room > 0 && char.IsHighSurrogate(text[room - 1]) ? room - 1 : room;
                            line.Append(text[..kept]);
                            onLine(line.ToString());
                            line.Clear();
                            discarding = true;
                            incomplete = true;
                        }
                    }

                    if (end < 0)
                    {
                        break;
                    }

                    if (!discarding)
                    {
                        onLine(line.ToString());
                        line.Clear();
                    }

                    discarding = false;
                    if (chunk[end] == '\r')
                    {
                        if (end + 1 == chunk.Length)
                        {
                            afterCarriageReturn = true;
                        }
                        else if (chunk[end + 1] == '\n')
                        {
                            end++;
                        }
                    }

                    chunk = chunk[(end + 1)..];
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The drain budget ran out and the stream was closed (or its read cancelled) under this read; a pending line is delivered below.
            incomplete |= line.Length > 0;
        }

        if (line.Length > 0)
        {
            onLine(line.ToString());
        }

        return incomplete;
    }
}

namespace XE_Local_AI_Engine.Client.Services.Sandbox.LiveOutput;

using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Common;

/// <summary>A sandbox output observer that redacts each line and delivers bounded, coalesced batches plus a bounded tail for a live view.</summary>
/// <remarks>
///     Redaction strips ANSI/VT sequences and control characters, replaces the workspace root with <c>&lt;workspace&gt;</c>, then the
///     <see cref="CredentialRedaction" /> patterns. Batches go out at most once per flush interval, under the batch and live caps;
///     a line that does not fit is counted as skipped. The callback runs under this instance's lock: it must enqueue and return.
///     The tail keeps raw lines and redacts them when read, so a flood past the live cap costs the pump no regex work.
/// </remarks>
public sealed partial class LiveCommandOutput : ISandboxOutputObserver, IDisposable
{
    private const string WorkspacePlaceholder = "<workspace>";
    private const string TruncationMarker = " [line truncated]";
    private const string WithheldLine = "[line withheld: redaction failed]";

    private readonly LiveCommandOutputLimits _limits;
    private readonly Action<LiveCommandOutputBatch> _onBatch;
    private readonly TimeProvider _timeProvider;
    private readonly string? _workspaceRoot;
    private readonly ILogger _logger;
    private readonly ITimer _flushTimer;
    private readonly Lock _sync = new();
    private readonly List<LiveCommandOutputLine> _pending = [];
    private readonly Queue<LiveCommandOutputLine> _rawTail = new();
    private int _pendingChars;
    private long _liveChars;
    private int _rawTailChars;
    private int _skippedLines;
    private int _deliveredSkippedLines;
    private long _lastFlushTimestamp;
    private bool _batchFull;
    private bool _timerArmed;
    private bool _completed;
    private bool _deliveryFaulted;

    /// <param name="workspaceRoot">The sandbox working root as the child prints it; blank skips the root replacement.</param>
    /// <param name="onBatch">Receives each batch, on a pump or timer thread, under this instance's lock; it must not block.</param>
    public LiveCommandOutput(LiveCommandOutputLimits limits,
        string? workspaceRoot,
        Action<LiveCommandOutputBatch> onBatch,
        TimeProvider timeProvider,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(onBatch);
        ArgumentNullException.ThrowIfNull(timeProvider);
        // Batch and tail must each hold the longest marked line, or such a line could never be shown.
        var longestLine = limits.MaxLineChars + TruncationMarker.Length + 1;
        if (limits.FlushInterval <= TimeSpan.Zero
            || limits.MaxLineChars <= 0
            || limits.MaxLiveChars <= 0
            || limits.MaxBatchChars < longestLine
            || limits.MaxTailChars < longestLine)
        {
            throw new ArgumentOutOfRangeException(nameof(limits),
                "The flush interval, line and live caps must be positive, and the batch and tail caps must hold one marked line.");
        }

        _limits = limits;
        _onBatch = onBatch;
        _timeProvider = timeProvider;
        _workspaceRoot = string.IsNullOrEmpty(workspaceRoot) ? null : workspaceRoot;
        _logger = logger ?? NullLogger.Instance;
        _lastFlushTimestamp = timeProvider.GetTimestamp();
        _flushTimer = timeProvider.CreateTimer(static state => ((LiveCommandOutput)state!).OnFlushTimer(),
            this,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
    }

    /// <summary>All lines not delivered live so far: past the live cap, or not fitting a batch before the next flush.</summary>
    public int SkippedLines
    {
        get
        {
            lock (_sync)
            {
                return _skippedLines;
            }
        }
    }

    public void OnLine(SandboxOutputSource source, string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var window = Cut(line, _limits.MaxLineChars * 2);
        lock (_sync)
        {
            if (_completed)
            {
                return;
            }

            AddToTailLocked(source, window);
            if (!TryAddLiveLocked(source, window))
            {
                _skippedLines++;
                ArmTimerLocked();
            }
        }
    }

    /// <summary>The redacted last lines, oldest first, at most <see cref="LiveCommandOutputLimits.MaxTailChars" /> in total.</summary>
    public IReadOnlyList<LiveCommandOutputLine> GetTail()
    {
        LiveCommandOutputLine[] raw;
        lock (_sync)
        {
            raw = [.. _rawTail];
        }

        var tail = new List<LiveCommandOutputLine>(raw.Length);
        var chars = 0;
        for (var index = raw.Length - 1; index >= 0; index--)
        {
            var text = Redact(raw[index].Text);
            chars += text.Length + 1;
            if (chars > _limits.MaxTailChars)
            {
                break;
            }

            tail.Add(new LiveCommandOutputLine
            {
                Source = raw[index].Source,
                Text = text
            });
        }

        tail.Reverse();
        return tail;
    }

    /// <summary>Delivers the last batch (when anything is new) and stops; later lines are ignored. Idempotent.</summary>
    public void Complete()
    {
        lock (_sync)
        {
            if (_completed)
            {
                return;
            }

            FlushLocked();
            _completed = true;
        }

        _flushTimer.Dispose();
    }

    /// <summary>Stops without delivering what is pending.</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            _completed = true;
        }

        _flushTimer.Dispose();
    }

    /// <summary>Cuts to <paramref name="maxChars" /> plus the truncation marker, never between a surrogate pair.</summary>
    private static string Cut(string text, int maxChars)
    {
        if (text.Length <= maxChars)
        {
            return text;
        }

        var end = char.IsHighSurrogate(text[maxChars - 1]) ? maxChars - 1 : maxChars;
        return string.Concat(text.AsSpan(0, end), TruncationMarker);
    }

    // ANSI/VT: CSI, OSC (BEL- or ST-terminated, or cut), two-character escapes, then any remaining C0/C1 control except tab.
    [GeneratedRegex(@"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(?:\x07|\x1B\\)?|[@-Z\\-_])|[\x00-\x08\x0B-\x1F\x7F-\x9F]",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking | RegexOptions.ExplicitCapture, 100)]
    private static partial Regex TerminalControl();

    // simplified: a line is windowed to twice the cap before redaction, so a secret cut at that edge shows only if redaction first
    // shrank the window below the cap; closing that needs a streaming redactor.
    private string Redact(string window)
    {
        try
        {
            var safe = TerminalControl().Replace(window, string.Empty);
            if (_workspaceRoot is not null)
            {
                safe = safe.Replace(_workspaceRoot, WorkspacePlaceholder, StringComparison.Ordinal);
            }

            return Cut(CredentialRedaction.Redact(safe), _limits.MaxLineChars);
        }
        catch (RegexMatchTimeoutException)
        {
            return WithheldLine;
        }
    }

    private void AddToTailLocked(SandboxOutputSource source, string window)
    {
        _rawTail.Enqueue(new LiveCommandOutputLine
        {
            Source = source,
            Text = window
        });
        _rawTailChars += window.Length + 1;
        while (_rawTailChars > _limits.MaxTailChars && _rawTail.Count > 1)
        {
            _rawTailChars -= _rawTail.Dequeue().Text.Length + 1;
        }
    }

    private bool TryAddLiveLocked(SandboxOutputSource source, string window)
    {
        // Both early exits skip the regex work, which is what keeps a flood cheap for the pump.
        if (_liveChars >= _limits.MaxLiveChars || (_batchFull && !FlushIntervalElapsedLocked()))
        {
            return false;
        }

        var text = Redact(window);
        var cost = text.Length + 1;
        if (_pendingChars + cost > _limits.MaxBatchChars)
        {
            if (!FlushIntervalElapsedLocked())
            {
                _batchFull = true;
                return false;
            }

            FlushLocked();
        }

        if (_liveChars + cost > _limits.MaxLiveChars)
        {
            _liveChars = _limits.MaxLiveChars;
            return false;
        }

        _pending.Add(new LiveCommandOutputLine
        {
            Source = source,
            Text = text
        });
        _pendingChars += cost;
        _liveChars += cost;
        ArmTimerLocked();
        return true;
    }

    private bool FlushIntervalElapsedLocked()
    {
        return _timeProvider.GetElapsedTime(_lastFlushTimestamp) >= _limits.FlushInterval;
    }

    private void ArmTimerLocked()
    {
        if (!_timerArmed)
        {
            _timerArmed = true;
            _ = _flushTimer.Change(_limits.FlushInterval, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnFlushTimer()
    {
        lock (_sync)
        {
            if (_completed)
            {
                return;
            }

            // A callback already dispatched when a size flush re-armed the timer, or one that fired early, waits out the rest.
            var remaining = _limits.FlushInterval - _timeProvider.GetElapsedTime(_lastFlushTimestamp);
            if (remaining > TimeSpan.Zero)
            {
                _timerArmed = true;
                _ = _flushTimer.Change(remaining, Timeout.InfiniteTimeSpan);
                return;
            }

            FlushLocked();
        }
    }

    private void FlushLocked()
    {
        _lastFlushTimestamp = _timeProvider.GetTimestamp();
        _batchFull = false;
        if (_timerArmed)
        {
            _timerArmed = false;
            _ = _flushTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        if (_pending.Count == 0 && _skippedLines == _deliveredSkippedLines)
        {
            return;
        }

        var batch = new LiveCommandOutputBatch
        {
            Lines = [.. _pending],
            SkippedLines = _skippedLines
        };
        _pending.Clear();
        _pendingChars = 0;
        _deliveredSkippedLines = _skippedLines;

        try
        {
            _onBatch(batch);
        }
        catch (Exception exception)
        {
            // Never into the pump or a timer thread; later batches are still attempted.
            if (!_deliveryFaulted)
            {
                _deliveryFaulted = true;
                _logger.LogWarning(exception, "A live command output batch could not be delivered.");
            }
        }
    }
}

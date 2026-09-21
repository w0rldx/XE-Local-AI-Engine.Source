namespace XE_Local_AI_Engine.Client.Services.Integrations.Implementation;

using System.Text.Json;
using System.Threading.Channels;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Integrations.Tools;

/// <summary>One mapped event before the buffer mints its sequence. Plumbing between the pure half and the appending half.</summary>
internal sealed class IntegrationStreamEventDraft
{
    public required string Type { get; init; }

    public required string? ContentType { get; init; }

    public required JsonElement? Payload { get; init; }
}

/// <summary>
///     Turns the worker dispatcher's signals into integration stream events, in a pure static half and a per-run
///     instance half.
/// </summary>
/// <remarks>
///     It maps NO terminal event: <c>execution.completed</c>, <c>.failed</c> and <c>.cancelled</c> have exactly one producer,
///     the coordinator's terminal transaction, which runs after <see cref="DrainAsync" /> — so the terminal is provably the
///     highest sequence in the ring, which is what lets a reader stop on it. What each half owns: ADR 0008 ("The stream mapper's two halves").
///     simplified: one Lock around the cursor, the timestamp, hasEmitted and the closed latch, not the chat pump's
///     channel-plus-consumer split; move to that shape only if work arrives that must leave the dispatcher's thread.
/// </remarks>
internal sealed class IntegrationStreamEventMapper : IAsyncDisposable
{
    private readonly IIntegrationExecutionEventBuffer _buffer;
    private readonly TimeSpan _emitDebounce;
    private readonly Guid _executionId;
    private readonly IIntegrationExecutionStore _executions;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly Guid _invocationId;
    private readonly int _maxOutputBytes;

    // Unbounded on purpose: the durable subset from this path is tool.started/tool.completed and nothing else, so the run's tool-iteration cap bounds it in
    // practice. It must not DROP — the chat sink may, because chat repairs a drop with a reconcile frame, and the ten integration event types carry no repair.
    private readonly Channel<IntegrationStreamEvent> _persist =
        Channel.CreateUnbounded<IntegrationStreamEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

    private readonly Task _pump;
    private readonly Guid _sessionId;
    private readonly TimeProvider _timeProvider;

    private bool _closed;
    private int _emitCursor;
    private bool _hasEmitted;
    private long _lastEmitTimestamp;

    public IntegrationStreamEventMapper(IIntegrationExecutionEventBuffer buffer,
        IIntegrationExecutionStore executions,
        Guid executionId,
        Guid sessionId,
        Guid invocationId,
        int maxOutputBytes,
        TimeSpan emitDebounce,
        TimeProvider timeProvider,
        ILogger logger)
    {
        _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        _executions = executions ?? throw new ArgumentNullException(nameof(executions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _executionId = executionId;
        _sessionId = sessionId;
        _invocationId = invocationId;
        _maxOutputBytes = maxOutputBytes;
        _emitDebounce = emitDebounce;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _pump = PumpAsync();
    }

    /// <summary>
    ///     Everything the assistant channel emits for one snapshot, or <see langword="null" /> when the snapshot
    ///     carries no growth.
    /// </summary>
    /// <remarks>
    ///     The stale-snapshot guard is the whole subtlety: publication happens outside the dispatcher's own lock, so a
    ///     SHORTER snapshot can arrive after a longer one and an unguarded slice would throw.
    /// </remarks>
    public static IntegrationStreamEventDraft? Delta(string? streamedContent, int contentOffset)
    {
        var content = streamedContent ?? string.Empty;
        var slice = content.Length > contentOffset ? content[contentOffset..] : null;
        return slice is null ? null : new IntegrationStreamEventDraft { Type = IntegrationStreamEventTypes.AssistantDelta, ContentType = null, Payload = Text(slice) };
    }

    /// <summary>
    ///     The bounded backstop for a caller that attached late. The concatenated deltas are authoritative and are
    ///     never bounded to match — cutting them would drop answer text from the stream that carries it.
    /// </summary>
    public static IntegrationStreamEventDraft Completed(string? streamedContent, int maxOutputBytes) =>
        new() { Type = IntegrationStreamEventTypes.AssistantCompleted, ContentType = null, Payload = Text(TruncateToUtf8ByteBudget(streamedContent ?? string.Empty, maxOutputBytes)) };

    /// <summary>
    ///     Both phases map; nothing else on the payload crosses to an external caller. The result text itself is never
    ///     mapped — only the name and the outcome.
    /// </summary>
    public static IntegrationStreamEventDraft? ToolLifecycle(ToolCallLifecyclePayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        return payload.Phase switch
        {
            ToolCallLifecyclePhase.Requested => new IntegrationStreamEventDraft
            {
                Type = IntegrationStreamEventTypes.ToolStarted,
                ContentType = null,
                Payload = Json(new
                {
                    name = payload.ToolName
                })
            },
            ToolCallLifecyclePhase.Completed => new IntegrationStreamEventDraft
            {
                Type = IntegrationStreamEventTypes.ToolCompleted,
                ContentType = null,
                Payload = Json(new
                {
                    name = payload.ToolName,
                    ok = !payload.IsError && IsToolOutcomeOk(payload)
                })
            },
            _ => null
        };
    }

    /// <summary>
    ///     <c>ok</c> means "the call raised no exception" for every tool except <c>emit_output</c>, which is graded on
    ///     its RESULT as well.
    /// </summary>
    /// <remarks>
    ///     <see cref="ToolCallLifecyclePayload.IsError" /> is set from the function-invocation pipeline's EXCEPTION and nothing
    ///     else, and <c>emit_output</c> refuses by RETURNING a sentence on every policy path — a throw would end the model's turn
    ///     and replace that sentence with the pipeline's own — so without this grading a refused emit would look exactly like a
    ///     delivered one, bar the missing <c>external.output</c> frame. Scoped to that tool by name: every other tool keeps the
    ///     exception-only meaning of <c>ok</c>, and no tool-result text goes on the wire either way.
    /// </remarks>
    private static bool IsToolOutcomeOk(ToolCallLifecyclePayload payload) =>
        !string.Equals(payload.ToolName, EmitOutputToolDefinition.ToolName, StringComparison.Ordinal)
        || EmitOutputToolDefinition.IsDelivered(payload.Result);

    /// <summary>Cuts at a whole-rune boundary against a UTF-8 BYTE budget.</summary>
    /// <remarks>
    ///     A surrogate-only guard bounds nothing: a 3-byte CJK glyph is a single <see cref="char" />, so it would
    ///     overshoot by up to two bytes per character. Copied from <c>HostProcessExecutor</c>, which solved the same
    ///     problem for tool output.
    /// </remarks>
    public static string TruncateToUtf8ByteBudget(string value, int budget)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (budget <= 0)
        {
            return string.Empty;
        }

        var used = 0;
        var lastCharIndex = 0;
        var charIndex = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (used + rune.Utf8SequenceLength > budget)
            {
                break;
            }

            used += rune.Utf8SequenceLength;
            charIndex += rune.Utf16SequenceLength;
            lastCharIndex = charIndex;
        }

        return value[..lastCharIndex];
    }

    /// <summary>The handler the coordinator's existing subscription calls: it maps, appends and returns.</summary>
    /// <remarks>
    ///     The dispatcher raises synchronously on the producing thread, so awaiting a SQLite write here would stall the
    ///     runner's streaming loop for the length of that write.
    /// </remarks>
    public void OnInvocationStateChanged(object? sender, InvocationStateChangedEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        try
        {
            MapInvocationState(args.State);
        }
        catch (Exception exception)
        {
            // The dispatcher raises with a bare ?.Invoke on the runner's own thread, so a throw here would escape onto that thread and skip every later
            // subscriber. An event this mapper cannot record costs a frame, never the run: the terminal row and the persisted transcript live elsewhere.
            _logger.LogError(exception,
                "Mapping an invocation state change for integration execution {ExecutionId} failed; the stream loses this event.",
                _executionId);
        }
    }

    private void MapInvocationState(InvocationState state)
    {
        if (state.InvocationId != _invocationId)
        {
            return;
        }

        var isTerminal = state.Status is InvocationStatus.Completed or InvocationStatus.Failed or InvocationStatus.Cancelled;
        var content = state.StreamedContent;

        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            // The chat pump's three arms, all of them. The !hasEmitted arm is what makes the first token visible
            // without waiting a window; dropping it would also withhold every delta forever under a fake clock.
            if ((isTerminal || !_hasEmitted || _timeProvider.GetElapsedTime(_lastEmitTimestamp) >= _emitDebounce)
                && Delta(content, _emitCursor) is { } delta)
            {
                _emitCursor = content.Length;
                _hasEmitted = true;
                _lastEmitTimestamp = _timeProvider.GetTimestamp();
                _ = AppendLocked(delta);
            }

            if (!isTerminal)
            {
                return;
            }

            if (state.Status == InvocationStatus.Completed)
            {
                _ = AppendLocked(Completed(content, _maxOutputBytes));
            }

            // Latched here so a late non-terminal snapshot cannot append an assistant.delta ABOVE the terminal the coordinator is about to write — an event
            // no reader would ever yield, and a LastSequence out of step with the row.
            _closed = true;
        }
    }

    /// <summary>The tool half of the same subscription. Persisted, unlike the assistant half.</summary>
    public void OnToolCallLifecycleChanged(object? sender, ToolCallLifecycleChangedEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        try
        {
            MapToolLifecycle(args.Payload);
        }
        catch (Exception exception)
        {
            // Same reason as the assistant half: this runs on the runner's thread behind a bare ?.Invoke.
            _logger.LogError(exception,
                "Mapping a tool lifecycle change for integration execution {ExecutionId} failed; the stream loses this event.",
                _executionId);
        }
    }

    private void MapToolLifecycle(ToolCallLifecyclePayload payload)
    {
        if (payload.InvocationId != _invocationId || ToolLifecycle(payload) is not { } draft)
        {
            return;
        }

        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            var streamEvent = AppendLocked(draft);
            _ = _persist.Writer.TryWrite(streamEvent);
        }
    }

    /// <summary>
    ///     The coordinator's one hook, awaited immediately before its terminal transaction: it latches the handlers
    ///     shut, closes the channel and awaits the pump.
    /// </summary>
    /// <remarks>
    ///     Every <c>tool.*</c> row is therefore committed and every <c>assistant.*</c> event is in the ring before the
    ///     terminal takes the last sequence. A failure here is a RUN failure and is rethrown: a lost <c>tool.*</c> row
    ///     means the persisted transcript is incomplete, so the run cannot honestly be reported as completed.
    /// </remarks>
    public Task DrainAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _closed = true;
        }

        _ = _persist.Writer.TryComplete();
        return _pump.WaitAsync(cancellationToken);
    }

    /// <summary>
    ///     Releases the pump on the paths that never reach the drain — a run rejected before it started, or a fault on
    ///     the way there.
    /// </summary>
    /// <remarks>
    ///     The failure is not re-surfaced here: whoever awaited <see cref="DrainAsync" /> already saw it, and a run
    ///     that never streamed has nothing to report.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _closed = true;
        }

        _ = _persist.Writer.TryComplete();
        try
        {
            await _pump;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Surfaced by DrainAsync when the run got that far.
        }
    }

    private static JsonElement Text(string value) =>
        Json(new
        {
            text = value
        });

    private static JsonElement Json<T>(T value) =>
        JsonSerializer.SerializeToElement(value, JsonOptions);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private IntegrationStreamEvent AppendLocked(IntegrationStreamEventDraft draft) =>
        _buffer.Append(_executionId, _sessionId, draft.Type, draft.ContentType, draft.Payload);

    private async Task PumpAsync()
    {
        // CancellationToken.None throughout: these rows belong to a sequence the buffer has already published, so
        // abandoning the write would leave a visible event with no durable row behind it.
        await foreach (var streamEvent in _persist.Reader.ReadAllAsync(CancellationToken.None))
        {
            await _executions.AppendEventAsync(new IntegrationEventAppend
            {
                EventId = Guid.NewGuid(),
                ExecutionId = streamEvent.ExecutionId,
                Sequence = streamEvent.Sequence,
                EventType = streamEvent.Type,
                DetailJson = streamEvent.Payload?.GetRawText(),
                OccurredAtUtc = streamEvent.OccurredAtUtc
            },
                                 CancellationToken.None);
        }
    }
}

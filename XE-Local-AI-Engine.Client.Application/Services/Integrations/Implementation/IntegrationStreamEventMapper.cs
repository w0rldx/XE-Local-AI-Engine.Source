namespace XE_Local_AI_Engine.Client.Services.Integrations.Implementation;

using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Events;

/// <summary>One mapped event before the buffer mints its sequence. Plumbing between the pure half and the appending half.</summary>
internal sealed record IntegrationStreamEventDraft(string Type, string? ContentType, JsonElement? Payload);

/// <summary>
///     Turns the worker dispatcher's signals into integration stream events, in two halves that are deliberately not
///     the same thing.
///     <para>
///         <b>The pure half</b> is the static methods: dispatcher args plus the caller's cursor in, a draft or
///         <see langword="null" /> out. No field, no buffer, no database, and therefore testable without a host.
///     </para>
///     <para>
///         <b>The per-run half</b> is an instance the coordinator builds inside its run scope and hangs on the ONE
///         subscription lifetime the coordinator already opens before the lease. It owns the emit cursor, the debounce
///         clock and the closed latch behind a single lock, appends to the ring, and pumps <c>tool.*</c> rows to the
///         store off a channel.
///     </para>
///     <para>
///         <b>It maps no terminal event.</b> <c>execution.completed</c>, <c>.failed</c> and <c>.cancelled</c> have
///         exactly one producer — the coordinator's terminal transaction, which runs after
///         <see cref="DrainAsync" />. That ordering is what makes the terminal provably the highest sequence in the
///         ring, which is in turn what lets a reader stop on it.
///     </para>
/// </summary>
/// <remarks>
///     ponytail: one Lock around cursor + timestamp + hasEmitted + closed latch, not the chat pump's
///     channel-plus-consumer split. Four fields and an Append do not need a task lifetime. Move to the channel shape if
///     the mapper ever grows work that must not run on the dispatcher's thread.
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

    // Unbounded on purpose: the durable subset from this path is tool.started/tool.completed and nothing else, so it is
    // bounded in practice by the run's tool-iteration cap. It must not DROP — the chat sink can, because chat repairs a
    // drop with a reconcile frame, and the ten integration event types carry no such repair.
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
    ///     Everything the assistant channel emits for one snapshot, or <see langword="null" /> when the snapshot carries
    ///     no growth. The stale-snapshot guard is the whole subtlety: publication happens outside the dispatcher's own
    ///     lock, so a SHORTER snapshot can arrive after a longer one and an unguarded slice would throw.
    /// </summary>
    public static IntegrationStreamEventDraft? Delta(string? streamedContent, int contentOffset)
    {
        var content = streamedContent ?? string.Empty;
        var slice = content.Length > contentOffset ? content[contentOffset..] : null;
        return slice is null ? null : new IntegrationStreamEventDraft(IntegrationStreamEventTypes.AssistantDelta, ContentType: null, Text(slice));
    }

    /// <summary>
    ///     The bounded backstop for a caller that attached late. The concatenated deltas are authoritative and are
    ///     never bounded to match — cutting them would drop answer text from the stream that carries it.
    /// </summary>
    public static IntegrationStreamEventDraft Completed(string? streamedContent, int maxOutputBytes) =>
        new(IntegrationStreamEventTypes.AssistantCompleted, ContentType: null, Text(TruncateToUtf8ByteBudget(streamedContent ?? string.Empty, maxOutputBytes)));

    /// <summary>Both phases map; nothing else on the payload crosses to an external caller.</summary>
    public static IntegrationStreamEventDraft? ToolLifecycle(ToolCallLifecyclePayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        return payload.Phase switch
        {
            ToolCallLifecyclePhase.Requested => new IntegrationStreamEventDraft(IntegrationStreamEventTypes.ToolStarted,
                ContentType: null,
                Json(new
                {
                    name = payload.ToolName
                })),
            ToolCallLifecyclePhase.Completed => new IntegrationStreamEventDraft(IntegrationStreamEventTypes.ToolCompleted,
                ContentType: null,
                Json(new
                {
                    name = payload.ToolName,
                    ok = !payload.IsError
                })),
            _ => null
        };
    }

    /// <summary>
    ///     Cuts at a whole-rune boundary against a UTF-8 BYTE budget. A surrogate-only guard bounds nothing: a 3-byte
    ///     CJK glyph is a single <see cref="char" />, so it would overshoot by up to two bytes per character. Copied
    ///     from <c>HostProcessExecutor</c>, which solved the same problem for tool output.
    /// </summary>
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

    /// <summary>
    ///     The handler the coordinator's existing subscription calls. It maps, appends and returns: the dispatcher
    ///     raises synchronously on the producing thread, so awaiting a SQLite write here would stall the runner's
    ///     streaming loop for the length of that write.
    /// </summary>
    public void OnInvocationStateChanged(object? sender, InvocationStateChangedEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        try
        {
            MapInvocationState(args.State);
        }
        catch (Exception exception)
        {
            // The dispatcher raises with a bare ?.Invoke on the runner's own thread, so a throw here would escape onto
            // that thread and skip every later subscriber. An event this mapper cannot record costs a frame, never the
            // run: the terminal row and the persisted transcript are written elsewhere.
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

            // Latched here so a late non-terminal snapshot cannot append an assistant.delta ABOVE the terminal the
            // coordinator is about to write — an event no reader would ever yield, and a LastSequence out of step with
            // the row.
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
    ///     The coordinator's one hook, awaited immediately before its terminal transaction. It latches the handlers
    ///     shut, closes the channel and awaits the pump, so every <c>tool.*</c> row is committed and every
    ///     <c>assistant.*</c> event is in the ring before the terminal takes the last sequence.
    ///     <para>
    ///         A failure here is a RUN failure and is rethrown: a lost <c>tool.*</c> row means the persisted transcript
    ///         is incomplete, so the run cannot honestly be reported as completed.
    ///     </para>
    /// </summary>
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
    ///     the way there. The failure is not re-surfaced here: whoever awaited <see cref="DrainAsync" /> already saw it,
    ///     and a run that never streamed has nothing to report.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _closed = true;
        }

        _ = _persist.Writer.TryComplete();
        try
        {
            await _pump.ConfigureAwait(false);
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
        await foreach (var streamEvent in _persist.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
        {
            await _executions.AppendEventAsync(new IntegrationEventAppend(Guid.NewGuid(),
                        streamEvent.ExecutionId,
                        streamEvent.Sequence,
                        streamEvent.Type,
                        streamEvent.Payload?.GetRawText(),
                        streamEvent.OccurredAtUtc),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
    }
}

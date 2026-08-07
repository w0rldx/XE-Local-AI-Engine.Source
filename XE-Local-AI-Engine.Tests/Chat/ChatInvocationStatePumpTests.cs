namespace XE_Local_AI_Engine.Tests.Chat;

using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The pump's emit cadence and persist cadence are decoupled and tracked by separate cursors. These tests pin the
///     three properties that decoupling has to preserve: the emitted deltas are contiguous (so the client can append
///     them and detect a gap from the offsets alone), a terminal never lands ahead of its own tail, and the SSE
///     cadence stays fast while the far more expensive persistence cadence lags well behind it.
/// </summary>
public sealed class ChatInvocationStatePumpTests
{
    [Test]
    public async Task PumpAsync_EmitsContiguousDeltaOffsetsAcrossAMultiChunkTurn()
    {
        // 50 ms between snapshots, comfortably past the 40 ms emit debounce, so every snapshot produces its own frame.
        var (events, _) = await DriveAsync(TimeSpan.FromMilliseconds(50),
                ["Hel", "Hello", "Hello wo", "Hello world"],
                terminalContent: "Hello world")
            .ConfigureAwait(false);

        var deltas = events.Where(streamEvent => streamEvent.Type == ChatStreamEventTypes.AssistantDelta).ToList();
        AssertEx.Equal(expected: 4, deltas.Count);

        var expectedFragments = new[]
        {
            "Hel",
            "lo",
            " wo",
            "rld"
        };
        for (var index = 0; index < deltas.Count; index++)
        {
            AssertEx.Equal(expectedFragments[index], deltas[index].Delta);
            // A live frame carries the increment and nothing else — never the accumulated text.
            AssertEx.Null(deltas[index].Content);
            AssertEx.Null(deltas[index].Reasoning);
        }

        // The client's gap detector depends on exactly this: the next delta begins where the previous one ended.
        AssertEx.Equal(expected: 0L, deltas[0].ContentOffset);
        for (var index = 1; index < deltas.Count; index++)
        {
            var previous = deltas[index - 1];
            AssertEx.Equal(previous.ContentOffset + (previous.Delta?.Length ?? 0), deltas[index].ContentOffset);
        }

        // And the deltas reassemble into exactly what the terminal carries, which is the whole point of the protocol.
        var terminal = events[^1];
        AssertEx.Equal(ChatStreamEventTypes.AssistantCompleted, terminal.Type);
        AssertEx.Equal("Hello world", string.Concat(deltas.Select(delta => delta.Delta)));
        AssertEx.Equal(AssertEx.NotNull(terminal.Content), string.Concat(deltas.Select(delta => delta.Delta)));
    }

    [Test]
    public async Task PumpAsync_WhenATerminalFollowsADebouncedChunk_EmitsTheTailDeltaBeforeTheTerminal()
    {
        // 10 ms steps keep the second chunk inside the emit debounce, so it is deferred — and the terminal must flush
        // that tail before it lands. Without this the client would be relying on the terminal's own content to
        // correct itself on every single turn, rather than only after a real fault.
        var (events, _) = await DriveAsync(TimeSpan.FromMilliseconds(10),
                ["Hello", "Hello world"],
                terminalContent: "Hello world")
            .ConfigureAwait(false);

        var deltas = events.Where(streamEvent => streamEvent.Type == ChatStreamEventTypes.AssistantDelta).ToList();
        AssertEx.Equal(expected: 2, deltas.Count);
        AssertEx.Equal("Hello", deltas[0].Delta);
        AssertEx.Equal(" world", deltas[1].Delta);
        AssertEx.Equal(expected: 5L, deltas[1].ContentOffset);

        // The tail delta is emitted BEFORE the terminal, so the client's accumulated text already agrees with it.
        AssertEx.Equal(ChatStreamEventTypes.AssistantCompleted, events[^1].Type);
        AssertEx.Equal(ChatStreamEventTypes.AssistantDelta, events[^2].Type);
        AssertEx.True(deltas[1].Sequence < events[^1].Sequence, "The tail delta must be sequenced before the terminal.");
    }

    [Test]
    public async Task PumpAsync_BoundsTheEmitCadenceByEmitDebounceWhileThePersistCadenceLagsFurtherBehind()
    {
        // Twenty snapshots, 10 chars each, one every 10 ms — 200 ms and 200 characters of turn.
        var snapshots = Enumerable.Range(1, 20).Select(chunk => new string('x', chunk * 10)).ToArray();

        var (events, flushes) = await DriveAsync(TimeSpan.FromMilliseconds(10), snapshots, terminalContent: snapshots[^1]).ConfigureAwait(false);

        var deltas = events.Where(streamEvent => streamEvent.Type == ChatStreamEventTypes.AssistantDelta).ToList();

        // Every frame after the first is at least one debounce window past the previous one. The timestamps come from
        // the same virtual clock the pump gates on, so this is the cadence bound itself, not a proxy for it.
        AssertEx.True(deltas.Count is > 1 and < 20, $"Twenty snapshots must coalesce into a handful of frames, got {deltas.Count}.");
        for (var index = 1; index < deltas.Count; index++)
        {
            AssertEx.True(deltas[index].OccurredAtUtc - deltas[index - 1].OccurredAtUtc >= 40,
                "Live delta frames must be spaced by at least EmitDebounceMs.");
        }

        // Persistence lags much further: at 200 characters the turn never reaches the 512-character growth floor and
        // never reaches the 2 s ceiling, so it writes only the mandatory first partial and the terminal's flush. That
        // gap between 'sent' and 'written' is exactly what decoupling the two cadences buys.
        AssertEx.True(flushes.Count < deltas.Count, $"Persistence ({flushes.Count} flushes) must lag the emit cadence ({deltas.Count} frames).");
        AssertEx.Equal(expected: 2, flushes.Count);

        // No content is lost by lagging: the terminal still carries the whole turn, and the deltas reassemble into it.
        AssertEx.Equal(snapshots[^1], string.Concat(deltas.Select(delta => delta.Delta)));
    }

    [Test]
    public async Task PumpAsync_EmitsReasoningDeltasWithTheirOwnOffsets()
    {
        var clock = new SteppingClock(DateTimeOffset.UnixEpoch);
        var recordingPump = new RecordingInvocationPump();
        var correlation = new NodeChatMessageCorrelation(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        var states = new List<InvocationState>
        {
            NewState(correlation, "Hi", "Think", InvocationStatus.Running),
            NewState(correlation, "Hi there", "Think harder", InvocationStatus.Running),
            NewState(correlation, "Hi there", "Think harder", InvocationStatus.Completed)
        };

        var events = await RunAsync(recordingPump, clock, correlation, states, TimeSpan.FromMilliseconds(50)).ConfigureAwait(false);
        var deltas = events.Where(streamEvent => streamEvent.Type == ChatStreamEventTypes.AssistantDelta).ToList();

        AssertEx.Equal(expected: 2, deltas.Count);
        AssertEx.Equal("Hi", deltas[0].Delta);
        AssertEx.Equal("Think", deltas[0].ReasoningDelta);
        AssertEx.Equal(expected: 0L, deltas[0].ContentOffset);
        AssertEx.Equal(expected: 0L, deltas[0].ReasoningOffset);

        AssertEx.Equal(" there", deltas[1].Delta);
        AssertEx.Equal(" harder", deltas[1].ReasoningDelta);
        // Content and reasoning advance on independent offsets, so a turn that streams both stays diffable on each.
        AssertEx.Equal(expected: 2L, deltas[1].ContentOffset);
        AssertEx.Equal(expected: 5L, deltas[1].ReasoningOffset);
    }

    private static async Task<(List<ChatStreamEvent> Events, List<string> Flushes)> DriveAsync(TimeSpan step,
        IReadOnlyList<string> contentSnapshots,
        string terminalContent)
    {
        var clock = new SteppingClock(DateTimeOffset.UnixEpoch);
        var recordingPump = new RecordingInvocationPump();
        var correlation = new NodeChatMessageCorrelation(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        var states = contentSnapshots.Select(content => NewState(correlation, content, string.Empty, InvocationStatus.Running)).ToList();
        states.Add(NewState(correlation, terminalContent, string.Empty, InvocationStatus.Completed));

        var events = await RunAsync(recordingPump, clock, correlation, states, step).ConfigureAwait(false);
        return (events, recordingPump.Flushes);
    }

    private static async Task<List<ChatStreamEvent>> RunAsync(RecordingInvocationPump recordingPump,
        SteppingClock clock,
        NodeChatMessageCorrelation correlation,
        IReadOnlyList<InvocationState> states,
        TimeSpan step)
    {
        var sink = new CollectingSink();
        var pump = new ChatInvocationStatePump(recordingPump, clock);

        await pump.PumpAsync(new SteppingStateReader(states, clock, step),
                      sink,
                      correlation,
                      "model-x",
                      new NodeChatStreamSequence(),
                      new NodeChatPartAccumulator(),
                      onTerminal: null,
                      CancellationToken.None)
                  .ConfigureAwait(false);

        return sink.Events;
    }

    private static InvocationState NewState(NodeChatMessageCorrelation correlation,
        string content,
        string reasoning,
        InvocationStatus status)
    {
        return new InvocationState
        {
            InvocationId = correlation.RequestId,
            ConversationId = correlation.ConversationId,
            Status = status,
            StreamedContent = content,
            StreamedThinkingContent = reasoning
        };
    }

    // Collects everything the pump emits, in order. The pump only ever writes to its sink, so the read side and the
    // budget behaviour (bounding, reconcile, detach) are out of scope here — they belong to the sink's own tests.
    private sealed class CollectingSink : IChatStreamEventSink
    {
        public List<ChatStreamEvent> Events { get; } = [];

        public ValueTask WriteAsync(ChatStreamEvent streamEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(streamEvent);
            return ValueTask.CompletedTask;
        }

        public bool TryWrite(ChatStreamEvent streamEvent)
        {
            Events.Add(streamEvent);
            return true;
        }

        public IAsyncEnumerable<ChatStreamEvent> ReadAllAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("These tests read the collected events directly.");
        }

        public void Detach()
        {
            // Nothing to detach: the collector has no consumer to lose.
        }

        public void Complete()
        {
            // Nothing to complete: the collected list is read after PumpAsync returns.
        }
    }

    /// <summary>
    ///     Hands the pump one snapshot per read and advances the virtual clock by a fixed step before each, so the
    ///     cadence gates are exercised deterministically. Deliberately never hands over a burst: the pump's
    ///     coalescing drain is not what these tests measure, and letting it fire would collapse the whole turn into
    ///     one frame.
    /// </summary>
    private sealed class SteppingStateReader(IReadOnlyList<InvocationState> states, SteppingClock clock, TimeSpan step) : ChannelReader<InvocationState>
    {
        private int _index;
        private bool _available;

        public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default)
        {
            if (_index >= states.Count)
            {
                return ValueTask.FromResult(false);
            }

            clock.Advance(step);
            _available = true;
            return ValueTask.FromResult(true);
        }

        public override bool TryRead([MaybeNullWhen(false)] out InvocationState item)
        {
            if (!_available || _index >= states.Count)
            {
                item = null;
                return false;
            }

            _available = false;
            item = states[_index++];
            return true;
        }
    }

    // Local deterministic clock (repo convention: per-test-file nested fake, no external time-testing package).
    // Overrides the timestamp pair as well as the wall clock, because the pump gates both cadences on
    // GetElapsedTime and stamps every event from GetUtcNow.
    private sealed class SteppingClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _utcNow = start;

        // One timestamp unit per DateTime tick, so GetElapsedTime resolves to exact virtual time.
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow()
        {
            return _utcNow;
        }

        public override long GetTimestamp()
        {
            return _utcNow.UtcTicks;
        }

        public void Advance(TimeSpan delta)
        {
            _utcNow = _utcNow.Add(delta);
        }
    }

    // An in-memory persistence pump that records what each flush would have rewritten. Returns a null Persisted when
    // nothing advanced, exactly as the real pump does, so the state pump's no-op flush path is exercised too.
    private sealed class RecordingInvocationPump : INodeChatInvocationPump
    {
        public List<string> Flushes { get; } = [];

        public Task<NodeChatPumpFlushResult> FlushDeltaAsync(NodeChatMessageCorrelation correlation,
            InvocationState state,
            NodeChatPumpCursor cursor,
            CancellationToken cancellationToken = default)
        {
            var content = state.StreamedContent;
            var reasoning = state.StreamedThinkingContent;

            if (content.Length <= cursor.Content.Length && reasoning.Length <= cursor.Reasoning.Length)
            {
                return Task.FromResult(new NodeChatPumpFlushResult(cursor, Persisted: null, ContentDelta: null, ReasoningDelta: null));
            }

            Flushes.Add(content);
            return Task.FromResult(new NodeChatPumpFlushResult(new NodeChatPumpCursor(content, reasoning),
                NewPersisted(correlation, content, reasoning, NodeChatMessageStatusValues.Streaming),
                content[cursor.Content.Length..],
                reasoning[cursor.Reasoning.Length..]));
        }

        public Task<NodeChatPumpTerminalResult> TerminalizeAsync(NodeChatMessageCorrelation correlation,
            InvocationState state,
            string? requestedModel,
            IReadOnlyList<NodeChatMessagePart>? parts = null,
            IReadOnlyList<NodeChatMessageSource>? sources = null)
        {
            return Task.FromResult(new NodeChatPumpTerminalResult(NewPersisted(correlation, state.StreamedContent, state.StreamedThinkingContent, NodeChatMessageStatusValues.Completed),
                NodeChatMessageStatusValues.Completed,
                ChatStreamEventTypes.AssistantCompleted));
        }

        public Task<NodeChatPumpTerminalResult> TerminalizeInterruptedAsync(NodeChatMessageCorrelation correlation,
            NodeChatPumpCursor cursor,
            bool wasCancelled)
        {
            var status = wasCancelled ? NodeChatMessageStatusValues.Cancelled : NodeChatMessageStatusValues.Interrupted;
            return Task.FromResult(new NodeChatPumpTerminalResult(NewPersisted(correlation, cursor.Content, cursor.Reasoning, status),
                status,
                ChatStreamEventMapper.TerminalEventType(status)));
        }

        private static NodeChatPersistedMessageDto NewPersisted(NodeChatMessageCorrelation correlation,
            string content,
            string reasoning,
            string status)
        {
            return new NodeChatPersistedMessageDto(correlation.MessageId,
                correlation.ConversationId,
                correlation.RequestId,
                Sequence: 1,
                Role: "assistant",
                Content: content,
                Reasoning: string.IsNullOrEmpty(reasoning) ? null : reasoning,
                Status: status,
                CreatedAtUtc: 0,
                UpdatedAtUtc: 0,
                Model: "model-x",
                Error: null,
                MetadataJson: null,
                InputCount: null,
                OutputCount: null);
        }
    }
}

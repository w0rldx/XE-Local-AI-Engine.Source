namespace XE_Local_AI_Engine.Tests.Integrations;

using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Integrations;
using XE_Local_AI_Engine.Client.Services.Integrations.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The dispatcher-to-envelope mapper, in its two halves. The pure half is called directly with constructed args —
///     no dispatcher, no host — and the per-run half is exercised as an instance, because that is exactly how the
///     coordinator uses it: one object per run, its handlers hung on the subscription the coordinator already owns.
///     <para>
///         The property that keeps coming back: <b>this mapper produces no terminal event</b>. The three
///         <c>execution.*</c> terminals have one producer, the coordinator's terminal transaction, which runs after the
///         drain — which is what makes the terminal the highest sequence in the ring.
///     </para>
/// </summary>
public sealed class IntegrationStreamEventMapperTests
{
    /// <summary>Test 10 — the non-terminal content row.</summary>
    [Test]
    public void Delta_SlicesTheSnapshotAtTheCallersCursor()
    {
        var draft = AssertEx.NotNull(IntegrationStreamEventMapper.Delta("Hello world", contentOffset: 5));

        AssertEx.Equal(IntegrationStreamEventTypes.AssistantDelta, draft.Type);
        AssertEx.Equal(" world", Text(draft));
    }

    /// <summary>Test 10 — the two tool rows.</summary>
    [Test]
    [Arguments(false, true)]
    [Arguments(true, false)]
    public void ToolLifecycle_MapsBothPhasesWithTheNameAndTheOutcome(bool isError, bool expectedOk)
    {
        var started = AssertEx.NotNull(IntegrationStreamEventMapper.ToolLifecycle(Payload(ToolCallLifecyclePhase.Requested, isError)));
        var completed = AssertEx.NotNull(IntegrationStreamEventMapper.ToolLifecycle(Payload(ToolCallLifecyclePhase.Completed, isError)));

        AssertEx.Equal(IntegrationStreamEventTypes.ToolStarted, started.Type);
        AssertEx.Equal("read_file", Field(started, "name"));
        AssertEx.False(started.Payload!.Value.TryGetProperty("ok", out _), "tool.started carries the name only.");

        AssertEx.Equal(IntegrationStreamEventTypes.ToolCompleted, completed.Type);
        AssertEx.Equal("read_file", Field(completed, "name"));
        AssertEx.Equal(expectedOk, completed.Payload!.Value.GetProperty("ok").GetBoolean());
    }

    /// <summary>Test 11 — a failed or cancelled run yields no terminal draft at all.</summary>
    [Test]
    [Arguments(InvocationStatus.Failed)]
    [Arguments(InvocationStatus.Cancelled)]
    public async Task Terminal_FailedOrCancelled_AppendsTheResidualDeltaAndNothingElse(InvocationStatus status)
    {
        await using var context = new MapperContext();
        context.Raise("partial answer", InvocationStatus.Running);
        context.Raise("partial answer and its tail", status);

        AssertEx.True((await context.TypesAsync()).SequenceEqual([IntegrationStreamEventTypes.AssistantDelta, IntegrationStreamEventTypes.AssistantDelta]),
            "The terminal event and its {category, summary} payload belong to the coordinator, not here.");
        AssertEx.Equal(" and its tail", Text((await context.EventsAsync())[^1]));
    }

    /// <summary>Test 12 — the stale-snapshot guard.</summary>
    [Test]
    public void Delta_WhenTheSnapshotIsShorterThanTheCursor_YieldsNothingRatherThanThrowing()
    {
        // Publication happens outside the dispatcher's own lock, so a SHORTER snapshot really can arrive after a
        // longer one; an unguarded content[cursor..] throws ArgumentOutOfRangeException on the runner's thread.
        AssertEx.Null(IntegrationStreamEventMapper.Delta("short", contentOffset: 99));
        AssertEx.Null(IntegrationStreamEventMapper.Delta("exact", contentOffset: 5), "A snapshot with no growth is not an empty delta, it is no delta.");
    }

    /// <summary>Test 13 — the truncation is rune-aware against a BYTE budget.</summary>
    [Test]
    [Arguments("漢", 3)]
    [Arguments("😀", 4)]
    public void Completed_CutsAtAWholeRuneBoundaryWithinTheByteBudget(string glyph, int glyphBytes)
    {
        // "aaa" + one multi-byte glyph, with a budget one byte short of the glyph: the whole glyph must go, never a
        // fragment of it, and never a replacement character.
        var text = new string('a', count: 3) + glyph + "tail";
        var budget = 3 + glyphBytes - 1;

        var cut = Text(IntegrationStreamEventMapper.Completed(text, budget));

        AssertEx.Equal("aaa", cut);
        AssertEx.True(Encoding.UTF8.GetByteCount(cut) <= budget);
        AssertEx.False(cut.Contains('\uFFFD', StringComparison.Ordinal), "A surrogate-only guard would leave a replacement character behind for the 3-byte glyph.");
    }

    /// <summary>Test 13 — and the whole glyph survives when the budget reaches it.</summary>
    [Test]
    public void Completed_KeepsTheGlyphWhenTheBudgetCoversIt()
    {
        var cut = Text(IntegrationStreamEventMapper.Completed("aaa😀tail", maxOutputBytes: 7));

        AssertEx.Equal("aaa😀", cut);
    }

    /// <summary>Test 14 — coalescing.</summary>
    [Test]
    public async Task Deltas_RaisedInsideOneWindow_CoalesceIntoASingleContiguousSlice()
    {
        await using var context = new MapperContext();
        // The first raise is immediate by contract (test 15), so it is what PRIMES the window the next twenty fall in.
        context.Raise("0", InvocationStatus.Running);

        var text = new StringBuilder("0");
        for (var index = 1; index <= 20; index++)
        {
            _ = text.Append(index % 10);
            context.Raise(text.ToString(), InvocationStatus.Running);
        }

        AssertEx.Equal(expected: 1, (await context.EventsAsync()).Count, "Twenty raises inside one debounce window must not mint twenty sequences.");

        context.Clock.Advance(TimeSpan.FromMilliseconds(41));
        context.Raise(text.ToString(), InvocationStatus.Running);

        AssertEx.Equal(expected: 2, (await context.EventsAsync()).Count);
        AssertEx.Equal(text.ToString()[1..], Text((await context.EventsAsync())[^1]), "Each snapshot carries the FULL accumulated content, so the coalesced delta is one contiguous slice.");
    }

    /// <summary>Test 15 — the !hasEmitted arm.</summary>
    [Test]
    public async Task FirstDelta_IsEmittedWithoutWaitingForTheWindow()
    {
        await using var context = new MapperContext();

        // The clock never moves: an elapsed-only condition would withhold this delta forever, and every fake-clock test
        // in this file would then be measuring the bug rather than the behaviour.
        context.Raise("first token", InvocationStatus.Running);

        AssertEx.Equal(expected: 1, (await context.EventsAsync()).Count);
        AssertEx.Equal("first token", Text((await context.EventsAsync())[0]));
    }

    /// <summary>Test 16 — the residual tail flushes before the completion backstop.</summary>
    [Test]
    public async Task Terminal_InsideTheDebounceWindow_FlushesThePendingDeltaBeforeAssistantCompleted()
    {
        await using var context = new MapperContext();
        context.Raise("one", InvocationStatus.Running);
        context.Raise("one two", InvocationStatus.Running);
        AssertEx.Equal(expected: 1, (await context.EventsAsync()).Count, "The second raise is inside the window.");

        context.Raise("one two three", InvocationStatus.Completed);

        AssertEx.True((await context.TypesAsync())
                             .SequenceEqual([IntegrationStreamEventTypes.AssistantDelta, IntegrationStreamEventTypes.AssistantDelta, IntegrationStreamEventTypes.AssistantCompleted]));
        AssertEx.Equal(" two three", Text((await context.EventsAsync())[1]), "A terminal emits its tail unconditionally, so the concatenated deltas equal the final text.");
        AssertEx.Equal("one two three", Text((await context.EventsAsync())[2]));
    }

    /// <summary>Test 17 — the closed latch, terminal arm.</summary>
    [Test]
    public async Task AfterATerminal_ALaterSnapshotAppendsNothing()
    {
        await using var context = new MapperContext();
        context.Raise("done", InvocationStatus.Completed);
        var afterTerminal = (await context.EventsAsync()).Count;

        context.Raise("done and then some more", InvocationStatus.Running);
        context.RaiseTool(ToolCallLifecyclePhase.Requested);

        AssertEx.Equal(afterTerminal, (await context.EventsAsync()).Count,
            "An event above the terminal sequence is one no reader will ever yield, and it desynchronises the row's LastSequence.");
        AssertEx.Empty((await context.TypesAsync()).Where(static type => type.StartsWith("execution.", StringComparison.Ordinal)),
            "The mapper appends no execution.* event on any path.");
    }

    /// <summary>Test 17a — the closed latch, drain arm.</summary>
    [Test]
    public async Task AfterTheDrain_ALaterSnapshotAppendsNothingAndPersistsNothing()
    {
        await using var context = new MapperContext();
        context.Raise("streaming", InvocationStatus.Running);
        await context.Mapper.DrainAsync(CancellationToken.None);
        var afterDrain = (await context.EventsAsync()).Count;

        // The coordinator unsubscribes only AFTER its terminal append, so a raise really can land in this window.
        context.Raise("streaming further", InvocationStatus.Running);
        context.RaiseTool(ToolCallLifecyclePhase.Completed);

        AssertEx.Equal(afterDrain, (await context.EventsAsync()).Count);
        AssertEx.Empty(context.Store.Events, "Nothing may be persisted after the drain returned, or the terminal is no longer the last row.");
    }

    /// <summary>Test 18 — invocation filtering.</summary>
    [Test]
    public async Task EventsForAnotherInvocation_AppendNothing()
    {
        await using var context = new MapperContext();

        context.Mapper.OnInvocationStateChanged(sender: null,
            new InvocationStateChangedEventArgs(new InvocationState
            {
                InvocationId = Guid.NewGuid(),
                Status = InvocationStatus.Running,
                StreamedContent = "someone else's turn"
            }));
        context.Mapper.OnToolCallLifecycleChanged(sender: null,
            new ToolCallLifecycleChangedEventArgs(new ToolCallLifecyclePayload
            {
                InvocationId = Guid.NewGuid(),
                ToolCallId = "call-1",
                ToolName = "read_file",
                Phase = ToolCallLifecyclePhase.Requested
            }));

        AssertEx.Empty(await context.EventsAsync(), "The dispatcher is node-wide; a run that filtered nothing would stream another turn's tokens to an external caller.");
    }

    /// <summary>Test 20 — the durable subset, and that the drain awaits it.</summary>
    [Test]
    public async Task Drain_PersistsToolEventsOnlyAtTheSequencesTheBufferMinted()
    {
        await using var context = new MapperContext();
        context.Raise("thinking", InvocationStatus.Running);
        context.RaiseTool(ToolCallLifecyclePhase.Requested);
        context.RaiseTool(ToolCallLifecyclePhase.Completed);
        context.Raise("thinking done", InvocationStatus.Completed);

        await context.Mapper.DrainAsync(CancellationToken.None);

        var persisted = context.Store.Events;
        AssertEx.True(persisted.Select(static row => row.EventType).SequenceEqual([IntegrationStreamEventTypes.ToolStarted, IntegrationStreamEventTypes.ToolCompleted]),
            "assistant.delta is per-token noise and assistant.completed duplicates the conversation's own assistant message; neither may reach the table.");

        var buffered = (await context.EventsAsync()).Where(static streamEvent => streamEvent.Type.StartsWith("tool.", StringComparison.Ordinal)).ToArray();
        AssertEx.True(persisted.Select(static row => row.Sequence).SequenceEqual(buffered.Select(static streamEvent => streamEvent.Sequence)),
            "A row is persisted at the sequence the buffer minted for it, never at one computed here.");
        AssertEx.Equal("""{"name":"read_file"}""", persisted[0].DetailJson);
    }

    /// <summary>Test 21 — persistence never runs on the thread that raised the event.</summary>
    [Test]
    public async Task Persistence_RunsOffTheRaisingThread()
    {
        await using var context = new MapperContext();
        using var blocked = new ManualResetEventSlim(initialState: false);
        using var reached = new ManualResetEventSlim(initialState: false);
        context.Store.OnAppendEvent = append =>
        {
            AssertEx.Equal(IntegrationStreamEventTypes.ToolStarted, append.EventType);
            reached.Set();
            AssertEx.True(blocked.Wait(TimeSpan.FromSeconds(10)));
        };

        context.RaiseTool(ToolCallLifecyclePhase.Requested);

        AssertEx.True(reached.Wait(TimeSpan.FromSeconds(10)), "The pump must have picked the event up.");
        AssertEx.Empty(context.Store.Events, "The raise returned while the write is still blocked, which is the property the channel exists for.");
        blocked.Set();

        await context.Mapper.DrainAsync(CancellationToken.None);
        AssertEx.Equal(expected: 1, context.Store.Events.Count);
    }

    /// <summary>Test 22 — a drain failure is a run failure, not a swallowed one.</summary>
    [Test]
    public async Task Drain_WhenTheStoreThrows_Rethrows()
    {
        await using var context = new MapperContext();
        context.Store.FailAppendEventWhen = static _ => true;
        context.RaiseTool(ToolCallLifecyclePhase.Requested);

        _ = await AssertEx.ThrowsAsync<DbUpdateException>(() => context.Mapper.DrainAsync(CancellationToken.None),
            "A lost tool row means the persisted transcript is incomplete, so the run cannot be reported as completed.");
    }

    /// <summary>
    ///     Test 43 — neither handler may throw. Both dispatcher raise sites are a bare <c>?.Invoke</c> on the runner's
    ///     own thread, so an escaping throw would take out the run's streaming loop and skip every later subscriber.
    /// </summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Handlers_WhenTheRingEntryIsGone_LogTheFailureInsteadOfThrowingOntoTheRunnersThread(bool tool)
    {
        await using var context = new MapperContext();
        context.RemoveEntry();

        if (tool)
        {
            context.RaiseTool(ToolCallLifecyclePhase.Requested);
        }
        else
        {
            context.Raise("Hello", InvocationStatus.Running);
        }

        AssertEx.True(context.Logger.HasEntry(LogLevel.Error, "the stream loses this event"),
            "An event the mapper cannot record costs a frame, and it has to say so; silence would hide a wiring bug.");
    }

    private static ToolCallLifecyclePayload Payload(ToolCallLifecyclePhase phase, bool isError) =>
        new()
        {
            InvocationId = Guid.NewGuid(),
            ToolCallId = "call-1",
            ToolName = "read_file",
            Phase = phase,
            IsError = isError
        };

    private static string Text(IntegrationStreamEventDraft draft) =>
        draft.Payload!.Value.GetProperty("text").GetString()!;

    private static string Text(IntegrationStreamEvent streamEvent) =>
        streamEvent.Payload!.Value.GetProperty("text").GetString()!;

    private static string Field(IntegrationStreamEventDraft draft, string name) =>
        draft.Payload!.Value.GetProperty(name).GetString()!;

    /// <summary>One per-run mapper over a real ring and a real store double, with a clock a test moves by hand.</summary>
    private sealed class MapperContext : IAsyncDisposable
    {
        private readonly IntegrationExecutionEventBuffer _buffer;
        private readonly Guid _executionId = Guid.NewGuid();
        private readonly Guid _invocationId = Guid.NewGuid();
        private readonly Guid _sessionId = Guid.NewGuid();

        public MapperContext()
        {
            _buffer = new IntegrationExecutionEventBuffer(Options.Create(new IntegrationOptions()), Clock);
            _ = _buffer.TryCreate(_executionId);
            Mapper = new IntegrationStreamEventMapper(_buffer,
                Store,
                _executionId,
                _sessionId,
                _invocationId,
                new IntegrationOptions().MaxOutputBytes,
                TimeSpan.FromMilliseconds(40),
                Clock,
                Logger);
        }

        public RecordingLogger<IntegrationStreamEventMapper> Logger { get; } = new();

        /// <summary>Drops the ring entry under the mapper, which is what makes an append throw.</summary>
        public void RemoveEntry() =>
            _buffer.Remove(_executionId);

        public ManualTimeProvider Clock { get; } = new();

        public FakeIntegrationExecutionStore Store { get; } = new();

        public IntegrationStreamEventMapper Mapper { get; }

        public void Raise(string streamedContent, InvocationStatus status) =>
            Mapper.OnInvocationStateChanged(sender: null,
                new InvocationStateChangedEventArgs(new InvocationState
                {
                    InvocationId = _invocationId,
                    Status = status,
                    StreamedContent = streamedContent
                }));

        public void RaiseTool(ToolCallLifecyclePhase phase) =>
            Mapper.OnToolCallLifecycleChanged(sender: null,
                new ToolCallLifecycleChangedEventArgs(new ToolCallLifecyclePayload
                {
                    InvocationId = _invocationId,
                    ToolCallId = "call-1",
                    ToolName = "read_file",
                    Phase = phase
                }));

        /// <summary>Everything the ring holds right now, read through the real reader rather than a test-only accessor.</summary>
        public async Task<IReadOnlyList<IntegrationStreamEvent>> EventsAsync()
        {
            var head = _buffer.LastSequence(_executionId);
            var events = new List<IntegrationStreamEvent>();
            if (head == 0)
            {
                return events;
            }

            // The mapper never appends a terminal event, so the enumerator has no natural end: it is stopped once the
            // head has been yielded. The ceiling makes a regression fail rather than hang.
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await foreach (var streamEvent in _buffer.ReadAsync(_executionId, sinceSequence: 0, cancellation.Token).ConfigureAwait(false))
                {
                    events.Add(streamEvent);
                    if (streamEvent.Sequence >= head)
                    {
                        await cancellation.CancelAsync();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // The stop signal above.
            }

            return events;
        }

        public async Task<IReadOnlyList<string>> TypesAsync() =>
            [.. (await EventsAsync()).Select(static streamEvent => streamEvent.Type)];

        public async ValueTask DisposeAsync()
        {
            await Mapper.DisposeAsync();
            _buffer.Dispose();
        }

    }
}

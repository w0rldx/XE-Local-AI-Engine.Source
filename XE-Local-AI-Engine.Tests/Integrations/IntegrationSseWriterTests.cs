namespace XE_Local_AI_Engine.Tests.Integrations;

using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Integrations;
using XE_Local_AI_Engine.Client.Services.Integrations.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The SSE writer. Two properties are load-bearing and neither is visible from the frames alone: every refusal
///     happens BEFORE a status line exists, because a status cannot be changed once the response has started; and the
///     caller's token ends forwarding without touching the run, so an integrator that closes its stream to poll does
///     not thereby cancel a generation.
/// </summary>
public sealed class IntegrationSseWriterTests
{
    /// <summary>Test 23 — the frame bytes, in the order SseFormatter emits them.</summary>
    [Test]
    public async Task WriteAsync_FramesAnEventAsEventThenDataThenId()
    {
        using var buffer = CreateBuffer();
        var executionId = Seed(buffer, out var sessionId);
        var streamEvent = buffer.Append(executionId, sessionId, IntegrationStreamEventTypes.ExecutionCompleted, contentType: null, payload: null);
        using var writer = CreateWriter(buffer);
        var context = BuildContext(out var body);

        var outcome = await writer.WriteAsync(context, executionId, sinceSequence: 0, context.RequestAborted);

        AssertEx.Equal(IntegrationSseWriteOutcome.Streamed, outcome);
        var expected = $"event: {IntegrationStreamEventTypes.ExecutionCompleted}\ndata: {JsonSerializer.Serialize(streamEvent, Web)}\nid: {streamEvent.Sequence}\n\n";
        AssertEx.Equal(expected, Encoding.UTF8.GetString(body.ToArray()),
            "The brief's id/event/data ordering is not achievable through the BCL formatter; R1-8 amends it to this order, which SSE treats as equivalent.");
    }

    /// <summary>Test 24 — the headers, and the buffering the proxy path also has to turn off.</summary>
    [Test]
    public async Task WriteAsync_SetsTheStreamingHeadersAndDisablesResponseBuffering()
    {
        using var buffer = CreateBuffer();
        var executionId = Seed(buffer, out var sessionId);
        _ = buffer.Append(executionId, sessionId, IntegrationStreamEventTypes.ExecutionCompleted, contentType: null, payload: null);
        using var writer = CreateWriter(buffer);
        var context = BuildContext(out _, out var responseFeature);

        _ = await writer.WriteAsync(context, executionId, sinceSequence: 0, context.RequestAborted);

        AssertEx.Equal("text/event-stream", context.Response.ContentType);
        AssertEx.Equal("no-cache", context.Response.Headers.CacheControl.ToString());
        AssertEx.Equal("no", context.Response.Headers["X-Accel-Buffering"].ToString());
        AssertEx.True(responseFeature.BufferingDisabled, "Without this the first tokens sit in a buffer and `curl -N` shows the whole answer at once.");
    }

    /// <summary>Test 25 — the keepalive clock, driven by hand.</summary>
    [Test]
    public async Task WriteAsync_WhenTheSourceIsSilent_WritesOneKeepaliveCommentEveryFifteenSeconds()
    {
        var clock = new ManualTimeProvider();
        using var buffer = CreateBuffer();
        var executionId = Seed(buffer, out _);
        using var writer = CreateWriter(buffer, clock);
        var context = BuildContext(out var body);

        var streaming = writer.WriteAsync(context, executionId, sinceSequence: 0, context.RequestAborted);

        // 31 s of silence: two whole windows, and a third that has not elapsed.
        await AdvanceAndSettleAsync(clock, body, TimeSpan.FromSeconds(16), expectedKeepalives: 1);
        await AdvanceAndSettleAsync(clock, body, TimeSpan.FromSeconds(15), expectedKeepalives: 2);

        _ = buffer.Append(executionId, Guid.NewGuid(), IntegrationStreamEventTypes.ExecutionCompleted, contentType: null, payload: null);
        _ = await streaming.WaitAsync(TimeSpan.FromSeconds(10));

        var text = Encoding.UTF8.GetString(body.ToArray());
        AssertEx.Equal(expected: 2, CountKeepalives(text), "A comment is the one keepalive form an EventSource ignores in silence.");
    }

    /// <summary>Test 26 — a caller that goes away ends forwarding and nothing else.</summary>
    [Test]
    public async Task WriteAsync_WhenTheCallerAborts_StopsForwardingWithoutTouchingTheRun()
    {
        using var buffer = CreateBuffer();
        var executionId = Seed(buffer, out var sessionId);
        _ = buffer.Append(executionId, sessionId, IntegrationStreamEventTypes.ExecutionStarted, contentType: null, payload: null);
        using var writer = CreateWriter(buffer);
        using var aborted = new CancellationTokenSource();
        var context = BuildContext(out var body);

        var streaming = writer.WriteAsync(context, executionId, sinceSequence: 0, aborted.Token);
        await AssertEx.EventuallyAsync(() => CountFrames(body) == 1, TimeSpan.FromSeconds(10), "The stream must be open and mid-flight before the caller goes away.");
        await aborted.CancelAsync();

        AssertEx.Equal(IntegrationSseWriteOutcome.Streamed, await streaming.WaitAsync(TimeSpan.FromSeconds(10)),
            "An abort is a normal end of forwarding, not a fault.");
        AssertEx.True(buffer.IsTracked(executionId), "The run's own state is untouched: nothing here cancels an invocation.");
    }

    /// <summary>Test 27 — a payload holding a newline still frames as one data: line.</summary>
    [Test]
    public async Task WriteAsync_WithAMultiLinePayload_StillEmitsASingleDataLine()
    {
        using var buffer = CreateBuffer();
        var executionId = Seed(buffer, out var sessionId);
        var payload = JsonSerializer.SerializeToElement(new
            {
                text = "line one\nline two"
            },
            Web);
        _ = buffer.Append(executionId, sessionId, IntegrationStreamEventTypes.AssistantDelta, contentType: null, payload);
        _ = buffer.Append(executionId, sessionId, IntegrationStreamEventTypes.ExecutionCompleted, contentType: null, payload: null);
        using var writer = CreateWriter(buffer);
        var context = BuildContext(out var body);

        _ = await writer.WriteAsync(context, executionId, sinceSequence: 0, context.RequestAborted);

        var text = Encoding.UTF8.GetString(body.ToArray());
        AssertEx.Equal(expected: 2, text.Split("data: ").Length - 1, "Compact JSON escapes the newline, so one event is always one data: line.");
        AssertEx.Contains(text, "line one\\nline two");
    }

    /// <summary>Test 28 — a gap raised after the 200 ends the response cleanly.</summary>
    [Test]
    public async Task WriteAsync_WhenTheRingMovesPastTheReaderMidStream_EndsTheResponseWithoutAFurtherFrame()
    {
        using var buffer = CreateBuffer();
        var executionId = Seed(buffer, out var sessionId);
        _ = buffer.Append(executionId, sessionId, IntegrationStreamEventTypes.ExecutionAccepted, contentType: null, payload: null);
        _ = buffer.Append(executionId, sessionId, IntegrationStreamEventTypes.ExecutionStarted, contentType: null, payload: null);
        using var writer = CreateWriter(buffer);
        var context = BuildContext(out var body);

        var streaming = writer.WriteAsync(context, executionId, sinceSequence: 0, context.RequestAborted);
        await AssertEx.EventuallyAsync(() => CountFrames(body) == 2, TimeSpan.FromSeconds(10));

        buffer.Remove(executionId);

        AssertEx.Equal(IntegrationSseWriteOutcome.Streamed, await streaming.WaitAsync(TimeSpan.FromSeconds(10)),
            "Letting the gap escape would reset the connection, which is the exact failure the 410-then-poll contract avoids.");
        AssertEx.Equal(expected: 2, CountFrames(body), "No frame says 'you were cut': none of the locked event types means that.");
        AssertEx.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    /// <summary>Test 29 — all three pre-header refusals, and that none of them touches the reader.</summary>
    [Test]
    [Arguments(false, 1L, 5L, 0L)]
    [Arguments(true, 4L, 9L, 0L)]
    [Arguments(true, 1L, 5L, 6L)]
    public async Task WriteAsync_RefusesBeforeAnyHeader(bool tracked, long floor, long head, long sinceSequence)
    {
        var stub = new StubBuffer
        {
            Tracked = tracked,
            FloorValue = floor,
            HeadValue = head
        };
        using var writer = CreateWriter(stub);
        var context = BuildContext(out var body);

        var outcome = await writer.WriteAsync(context, Guid.NewGuid(), sinceSequence, context.RequestAborted);

        AssertEx.Equal(IntegrationSseWriteOutcome.Gone, outcome);
        AssertEx.False(stub.ReadCalled, "Deciding this by starting the enumerator would surface the gap after the 200, where the status can no longer be set.");
        AssertEx.Equal(expected: 0L, body.Length);
        AssertEx.Equal(StatusCodes.Status200OK, context.Response.StatusCode, "The status is still the default, i.e. unset — the route is free to write 410.");
    }

    /// <summary>Test 29a — the ordinary first attach is served, not refused.</summary>
    [Test]
    public async Task WriteAsync_AtTheWindowEdge_Streams()
    {
        var stub = new StubBuffer
        {
            Tracked = true,
            FloorValue = 1,
            HeadValue = 1
        };
        using var writer = CreateWriter(stub);
        var context = BuildContext(out _);

        var outcome = await writer.WriteAsync(context, Guid.NewGuid(), sinceSequence: 0, context.RequestAborted);

        AssertEx.Equal(IntegrationSseWriteOutcome.Streamed, outcome, "A literal `sinceSequence < Floor` would 410 every first attach on a healthy execution.");
        AssertEx.True(stub.ReadCalled);
    }

    /// <summary>Test 29b — the open-stream cap, and that closing one stream frees exactly one slot.</summary>
    [Test]
    public async Task WriteAsync_WhenEveryStreamSlotIsHeld_ReturnsBusyWithoutTouchingTheResponse()
    {
        const int maxStreams = 2;
        using var buffer = CreateBuffer(maxTracked: maxStreams);
        using var writer = CreateWriter(buffer, maxTracked: maxStreams);
        var held = new List<(Task<IntegrationSseWriteOutcome> Streaming, Guid ExecutionId)>();
        for (var index = 0; index < maxStreams; index++)
        {
            var executionId = Seed(buffer, out var sessionId);
            _ = buffer.Append(executionId, sessionId, IntegrationStreamEventTypes.ExecutionStarted, contentType: null, payload: null);
            var context = BuildContext(out var body);
            var streaming = writer.WriteAsync(context, executionId, sinceSequence: 0, context.RequestAborted);
            await AssertEx.EventuallyAsync(() => CountFrames(body) == 1, TimeSpan.FromSeconds(10));
            held.Add((streaming, executionId));
        }

        var refusedContext = BuildContext(out var refusedBody);
        var refused = await writer.WriteAsync(refusedContext, held[0].ExecutionId, sinceSequence: 0, refusedContext.RequestAborted);

        AssertEx.Equal(IntegrationSseWriteOutcome.Busy, refused);
        AssertEx.Equal(expected: 0L, refusedBody.Length);

        // Close exactly one, and exactly one slot must come back.
        _ = buffer.Append(held[0].ExecutionId, Guid.NewGuid(), IntegrationStreamEventTypes.ExecutionCompleted, contentType: null, payload: null);
        _ = await held[0].Streaming.WaitAsync(TimeSpan.FromSeconds(10));

        var freedContext = BuildContext(out _);
        var freed = await writer.WriteAsync(freedContext, held[0].ExecutionId, sinceSequence: 0, freedContext.RequestAborted);
        AssertEx.Equal(IntegrationSseWriteOutcome.Streamed, freed, "The release runs in a finally, so a closed stream always gives its slot back.");

        _ = buffer.Append(held[1].ExecutionId, Guid.NewGuid(), IntegrationStreamEventTypes.ExecutionCompleted, contentType: null, payload: null);
        _ = await held[1].Streaming.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>
    ///     Test 43 — a dead peer on the keepalive write, with a move still parked. Two failures at once: the write path
    ///     caught only cancellation, and the enumerator was then disposed with that move in flight, which a
    ///     compiler-generated async iterator answers with <c>NotSupportedException</c> thrown outside every catch.
    /// </summary>
    [Test]
    public async Task WriteAsync_WhenTheBodyFailsWhileAMoveIsParked_SwallowsItAndDrainsTheReader()
    {
        var clock = new ManualTimeProvider();
        // Released ONLY by the writer's own cancellation: the caller never aborts here, so if the drain were not bounded
        // by the writer's own token this test would hang rather than fail.
        var stub = new ParkingBuffer(releaseOnCancellation: true);
        var logger = new RecordingLogger<IntegrationSseWriter>();
        using var writer = CreateWriter(stub, clock, logger: logger);
        using var body = new FaultingBody(static () => new IOException("The peer is gone."));
        var context = BuildContext(body);

        var streaming = writer.WriteAsync(context, Guid.NewGuid(), sinceSequence: 0, context.RequestAborted);
        await AdvanceToKeepaliveAsync(clock, body);

        AssertEx.Equal(IntegrationSseWriteOutcome.Streamed, await streaming.WaitAsync(TimeSpan.FromSeconds(10)),
            "Kestrel surfaces a dead peer as IOException as often as it does an abort, and on a started 200 there is no status left to say it with.");
        AssertEx.True(stub.Ended, "The outstanding move must be drained before the enumerator is disposed.");
        AssertEx.True(logger.HasEntry(LogLevel.Debug, "ended early"), "A disconnect is logged at debug, never thrown.");
    }

    /// <summary>Test 44 — the ordinary client-disconnect path: an abort landing while a move is parked.</summary>
    [Test]
    public async Task WriteAsync_WhenTheCallerAbortsWhileAMoveIsParked_DisposesTheEnumeratorCleanly()
    {
        var clock = new ManualTimeProvider();
        // This reader ignores cancellation, so the parked move is still in flight when the writer tears down — which is
        // the state the disposal bug needs, and which an abort-from-outside would only race into.
        var stub = new ParkingBuffer(releaseOnCancellation: false);
        using var aborted = new CancellationTokenSource();
        using var writer = CreateWriter(stub, clock);
        using var body = new FaultingBody(() =>
        {
            aborted.Cancel();
            return new OperationCanceledException(aborted.Token);
        });
        var context = BuildContext(body);

        var streaming = writer.WriteAsync(context, Guid.NewGuid(), sinceSequence: 0, aborted.Token);
        await AdvanceToKeepaliveAsync(clock, body);
        await stub.Cancelled.WaitAsync(TimeSpan.FromSeconds(10));
        stub.Release();

        AssertEx.Equal(IntegrationSseWriteOutcome.Streamed, await streaming.WaitAsync(TimeSpan.FromSeconds(10)),
            "An abort is a normal end of forwarding; the NotSupportedException from disposing a live enumerator is not, and it escapes onto a response that already sent 200.");
        AssertEx.True(stub.Ended, "The reader must have ended before the enumerator was disposed.");
    }

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>Arms the keepalive timer, then fires it — which is what puts the failing write next to a parked move.</summary>
    private static async Task AdvanceToKeepaliveAsync(ManualTimeProvider clock, FaultingBody body)
    {
        await AssertEx.EventuallyAsync(() => clock.ArmedTimerCount > 0, TimeSpan.FromSeconds(10), "The writer never armed its keepalive timer.");
        clock.Advance(TimeSpan.FromSeconds(16));
        await body.Attempted.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static async Task AdvanceAndSettleAsync(ManualTimeProvider clock, MemoryStream body, TimeSpan delta, int expectedKeepalives)
    {
        // Wait for the writer to arm its keepalive timer first: advancing past a window nothing is waiting on moves the
        // clock and produces nothing.
        await AssertEx.EventuallyAsync(() => clock.ArmedTimerCount > 0, TimeSpan.FromSeconds(10), "The writer never armed its keepalive timer.");
        clock.Advance(delta);
        await AssertEx.EventuallyAsync(() => CountKeepalives(Encoding.UTF8.GetString(body.ToArray())) == expectedKeepalives,
            TimeSpan.FromSeconds(10),
            $"Expected {expectedKeepalives} keepalive comments after {delta.TotalSeconds:0} s of silence.");
    }

    private static int CountKeepalives(string body) =>
        body.Split(": keepalive\n\n").Length - 1;

    private static int CountFrames(MemoryStream body) =>
        Encoding.UTF8.GetString(body.ToArray()).Split("\nid: ").Length - 1;

    private static Guid Seed(IntegrationExecutionEventBuffer buffer, out Guid sessionId)
    {
        sessionId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        AssertEx.True(buffer.TryCreate(executionId));
        return executionId;
    }

    private static IntegrationExecutionEventBuffer CreateBuffer(int maxTracked = 64) =>
        new(Options.Create(new IntegrationOptions
            {
                MaxTrackedExecutions = maxTracked
            }),
            TimeProvider.System);

    private static IntegrationSseWriter CreateWriter(IIntegrationExecutionEventBuffer buffer,
        TimeProvider? timeProvider = null,
        int maxTracked = 64,
        ILogger<IntegrationSseWriter>? logger = null) =>
        new(buffer,
            Options.Create(new IntegrationOptions
            {
                MaxTrackedExecutions = maxTracked
            }),
            timeProvider ?? TimeProvider.System,
            logger ?? NullLogger<IntegrationSseWriter>.Instance);

    private static DefaultHttpContext BuildContext(out MemoryStream responseBody) =>
        BuildContext(out responseBody, out _);

    private static DefaultHttpContext BuildContext(Stream responseBody)
    {
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpResponseBodyFeature>(new StubResponseBody(responseBody));
        context.Response.Body = responseBody;
        return context;
    }

    private static DefaultHttpContext BuildContext(out MemoryStream responseBody, out StubResponseBody responseFeature)
    {
        var context = new DefaultHttpContext();
        responseBody = new MemoryStream();
        responseFeature = new StubResponseBody(responseBody);
        context.Features.Set<IHttpResponseBodyFeature>(responseFeature);
        context.Response.Body = responseBody;
        return context;
    }

    /// <summary>Records whether the writer turned buffering off, which no header can show.</summary>
    private sealed class StubResponseBody(Stream stream) : IHttpResponseBodyFeature
    {
        public bool BufferingDisabled { get; private set; }

        public Stream Stream { get; } = stream;

        public PipeWriter Writer { get; } = PipeWriter.Create(stream);

        public void DisableBuffering() =>
            BufferingDisabled = true;

        public Task StartAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SendFileAsync(string path, long offset, long? count, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task CompleteAsync() =>
            Task.CompletedTask;
    }

    /// <summary>A response body that fails the way a peer that is gone does, and records that it was written to.</summary>
    private sealed class FaultingBody(Func<Exception> failure) : MemoryStream
    {
        public TaskCompletionSource Attempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _ = Attempted.TrySetResult();
            return ValueTask.FromException(failure());
        }
    }

    /// <summary>
    ///     A reader that parks until a test lets it go. It is a real compiler-generated async iterator on purpose:
    ///     disposing one of those with a <c>MoveNextAsync</c> in flight is what throws <c>NotSupportedException</c>, and
    ///     a hand-written enumerator would not reproduce it.
    /// </summary>
    private sealed class ParkingBuffer(bool releaseOnCancellation) : IIntegrationExecutionEventBuffer
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes when the reader's token is cancelled, i.e. when the writer has torn its stream down.</summary>
        public Task Cancelled => _cancelled.Task;

        /// <summary>Whether the parked move ran to an end, which it cannot have done if it was disposed under.</summary>
        public bool Ended { get; private set; }

        public void Release() =>
            _release.TrySetResult();

        public bool IsTracked(Guid executionId) =>
            true;

        public long Floor(Guid executionId) =>
            1;

        public long LastSequence(Guid executionId) =>
            1;

        public async IAsyncEnumerable<IntegrationStreamEvent> ReadAsync(Guid executionId,
            long sinceSequence,
            [EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            await using (cancellationToken.Register(() => _cancelled.TrySetResult()).ConfigureAwait(false))
            {
                try
                {
                    await (releaseOnCancellation ? _release.Task.WaitAsync(cancellationToken) : _release.Task).ConfigureAwait(false);
                }
                finally
                {
                    Ended = true;
                }
            }

            yield break;
        }

        public bool TryCreate(Guid executionId, long initialSequence = 0) =>
            throw new NotSupportedException();

        public void Remove(Guid executionId) =>
            throw new NotSupportedException();

        public IntegrationStreamEvent Append(Guid executionId, Guid sessionId, string type, string? contentType, JsonElement? payload) =>
            throw new NotSupportedException();

        public long Reserve(Guid executionId) =>
            throw new NotSupportedException();

        public void Publish(IntegrationStreamEvent streamEvent) =>
            throw new NotSupportedException();

        public void Abandon(Guid executionId, long sequence) =>
            throw new NotSupportedException();

        public long LowestPendingReservation(Guid executionId) =>
            throw new NotSupportedException();
    }

    /// <summary>
    ///     A buffer whose numbers a test dictates, so the pre-header arms can be driven independently — and so
    ///     "ReadAsync was never called" is an assertion rather than an inference.
    /// </summary>
    private sealed class StubBuffer : IIntegrationExecutionEventBuffer
    {
        public bool Tracked { get; init; }

        public long FloorValue { get; init; }

        public long HeadValue { get; init; }

        public bool ReadCalled { get; private set; }

        public bool IsTracked(Guid executionId) =>
            Tracked;

        public long Floor(Guid executionId) =>
            FloorValue;

        public long LastSequence(Guid executionId) =>
            HeadValue;

        public IAsyncEnumerable<IntegrationStreamEvent> ReadAsync(Guid executionId, long sinceSequence, CancellationToken cancellationToken = default)
        {
            ReadCalled = true;
            return Empty();
        }

        public bool TryCreate(Guid executionId, long initialSequence = 0) =>
            throw new NotSupportedException();

        public void Remove(Guid executionId) =>
            throw new NotSupportedException();

        public IntegrationStreamEvent Append(Guid executionId, Guid sessionId, string type, string? contentType, JsonElement? payload) =>
            throw new NotSupportedException();

        public long Reserve(Guid executionId) =>
            throw new NotSupportedException();

        public void Publish(IntegrationStreamEvent streamEvent) =>
            throw new NotSupportedException();

        public void Abandon(Guid executionId, long sequence) =>
            throw new NotSupportedException();

        public long LowestPendingReservation(Guid executionId) =>
            throw new NotSupportedException();

        private static async IAsyncEnumerable<IntegrationStreamEvent> Empty()
        {
            await Task.CompletedTask.ConfigureAwait(false);
            yield break;
        }
    }
}

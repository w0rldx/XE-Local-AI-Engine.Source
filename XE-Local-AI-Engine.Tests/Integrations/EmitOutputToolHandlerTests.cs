namespace XE_Local_AI_Engine.Tests.Integrations;

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.Integrations;
using XE_Local_AI_Engine.Client.Services.Integrations.Tools;
using XE_Local_AI_Engine.Client.Services.Integrations.Tools.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <c>emit_output</c>'s handler. Two properties carry it, and every test here is one of them: NOTHING is visible to
///     the caller before its row is durable, and exactly one of <c>Publish</c> or <c>Abandon</c> follows every
///     successful <c>Reserve</c> — a hole readers step over is legal, a reservation nobody resolves is a stall.
/// </summary>
public sealed class EmitOutputToolHandlerTests
{
    [Test]
    public async Task WithoutAnAmbientConversation_ReturnsTheOutOfScopeSentence()
    {
        using var fixture = new Fixture();

        // No BeginScope: this is the scheduler, a benchmark and every other caller that never seeds the ambient.
        var answer = await fixture.Handler.ExecuteAsync(Arguments()).ConfigureAwait(false);

        AssertEx.Contains(answer, "only works inside an integration execution");
        AssertEx.Empty(fixture.Executions.Events);
        AssertEx.Empty(fixture.Buffer.Published);
    }

    [Test]
    public async Task WithAConversationThatIsNotAnIntegrationSession_ReturnsTheOutOfScopeSentence()
    {
        // A throwaway conversation id, which is exactly what the scheduler and the benchmark executors pass.
        using var fixture = new Fixture();
        using var scope = AgentRunConversationContext.BeginScope(Guid.NewGuid());

        var answer = await fixture.Handler.ExecuteAsync(Arguments()).ConfigureAwait(false);

        AssertEx.Contains(answer, "only works inside an integration execution");
        AssertEx.Empty(fixture.Executions.Events);
    }

    [Test]
    public async Task WithNoRunningExecution_ReturnsASentenceAndEmitsNothing()
    {
        using var fixture = new Fixture(runningExecution: false);
        using var scope = AgentRunConversationContext.BeginScope(fixture.ConversationId);

        var answer = await fixture.Handler.ExecuteAsync(Arguments()).ConfigureAwait(false);

        AssertEx.Contains(answer, "No integration execution is currently running");
        AssertEx.Empty(fixture.Executions.Events);
        AssertEx.Empty(fixture.Buffer.Published);
    }

    [Test]
    public async Task CommitsThenPublishesAndIncrementsOutputCount()
    {
        using var fixture = new Fixture();
        using var scope = AgentRunConversationContext.BeginScope(fixture.ConversationId);

        var first = await fixture.Handler.ExecuteAsync(Arguments("""{"ok":true}""")).ConfigureAwait(false);
        var second = await fixture.Handler.ExecuteAsync(Arguments("""{"ok":false}""")).ConfigureAwait(false);

        AssertEx.Contains(first, "Output delivered to the caller");
        AssertEx.Contains(second, "Output delivered to the caller");
        AssertEx.Equal(expected: 2, fixture.Executions.Events.Count);
        AssertEx.Equal(expected: 2, fixture.Buffer.Published.Count);
        AssertEx.Equal(expected: 2, fixture.Row().OutputCount);
        AssertEx.True(fixture.Buffer.Published[1].Sequence > fixture.Buffer.Published[0].Sequence, "Sequences are monotonic per execution.");
        AssertEx.Equal(IntegrationStreamEventTypes.ExternalOutput, fixture.Buffer.Published[0].Type);
        AssertEx.Equal("application/json", fixture.Buffer.Published[0].ContentType, "A call that names no media type takes the default.");
    }

    [Test]
    public async Task TheEventCarriesTheComposedEnvelopeAndThePublishedFrameCarriesThePayloadVerbatim()
    {
        using var fixture = new Fixture();
        using var scope = AgentRunConversationContext.BeginScope(fixture.ConversationId);

        _ = await fixture.Handler.ExecuteAsync(Arguments("""{"reading":42}""", "text/plain")).ConfigureAwait(false);

        var detail = AssertEx.NotNull(fixture.Executions.Events[0].DetailJson);
        using var envelope = JsonDocument.Parse(detail);
        AssertEx.Equal("text/plain", envelope.RootElement.GetProperty("contentType").GetString());
        AssertEx.Equal(expected: 42, envelope.RootElement.GetProperty("payload").GetProperty("reading").GetInt32());
        var published = fixture.Buffer.Published[0].Payload ?? throw new AssertionException("The published frame carries the payload verbatim.");
        AssertEx.Equal(expected: 42, published.GetProperty("reading").GetInt32());
    }

    [Test]
    public async Task OverSizedPayload_ReturnsAnErrorAndEmitsNothing()
    {
        // Measured on the COMPOSED envelope, not the raw payload: the event's column is capped and encrypted, so a
        // payload just under the limit plus its wrapper would overrun a bound this handler claims to respect.
        using var fixture = new Fixture(maxOutputBytes: 200);
        using var scope = AgentRunConversationContext.BeginScope(fixture.ConversationId);

        var underByPayloadOverByEnvelope = $$"""{"text":"{{new string('x', count: 160)}}"}""";
        AssertEx.True(Encoding.UTF8.GetByteCount(underByPayloadOverByEnvelope) < 200, "The fixture needs a payload that only the envelope pushes over.");

        var answer = await fixture.Handler.ExecuteAsync(Arguments(underByPayloadOverByEnvelope)).ConfigureAwait(false);

        AssertEx.Contains(answer, "Nothing was delivered");
        AssertEx.Empty(fixture.Executions.Events);
        AssertEx.Empty(fixture.Buffer.Reserved);
        AssertEx.Equal(expected: 0, fixture.Row().OutputCount);
    }

    [Test]
    public async Task OutputsPastTheAggregateByteCap_ReturnASentenceAndEmitNothing()
    {
        using var fixture = new Fixture(maxOutputBytesPerExecution: 120);
        using var scope = AgentRunConversationContext.BeginScope(fixture.ConversationId);

        var accepted = await fixture.Handler.ExecuteAsync(Arguments("""{"a":1}""")).ConfigureAwait(false);
        var refused = await fixture.Handler.ExecuteAsync(Arguments($$"""{"b":"{{new string('y', count: 100)}}"}""")).ConfigureAwait(false);

        AssertEx.Contains(accepted, "Output delivered");
        AssertEx.Contains(refused, "nothing further was delivered");
        AssertEx.Equal(expected: 1, fixture.Executions.Events.Count);
        AssertEx.Equal(expected: 1, fixture.Row().OutputCount);

        // The pre-check runs BEFORE the reservation, so "nothing buffered" is provable rather than hoped for.
        AssertEx.Equal(expected: 1, fixture.Buffer.Reserved.Count, "An over-cap call must not even reserve a sequence.");
        AssertEx.Equal(expected: 1, fixture.Buffer.Published.Count);
        AssertEx.Empty(fixture.Buffer.Abandoned);
    }

    [Test]
    public async Task RepeatedCallsChargeEachPayloadExactlyOnce()
    {
        // The regression test for the deleted in-memory tally: the column is the only authority, and a tally on top of
        // it would double-count every committed call and refuse an execution early.
        using var fixture = new Fixture();
        using var scope = AgentRunConversationContext.BeginScope(fixture.ConversationId);

        for (var i = 0; i < 3; i++)
        {
            _ = await fixture.Handler.ExecuteAsync(Arguments("""{"a":1}""")).ConfigureAwait(false);
        }

        var perCall = Encoding.UTF8.GetByteCount("""{"contentType":"application/json","payload":{"a":1}}""");
        AssertEx.Equal(3L * perCall, fixture.Row().OutputBytes, "Three calls of n bytes charge exactly 3n.");
    }

    [Test]
    public async Task TheAggregateCapCountsPlaintextBytes_NotCiphertext()
    {
        using var fixture = new Fixture();
        using var scope = AgentRunConversationContext.BeginScope(fixture.ConversationId);

        _ = await fixture.Handler.ExecuteAsync(Arguments("""{"reading":42}""")).ConfigureAwait(false);

        var envelope = AssertEx.NotNull(fixture.Executions.Events[0].DetailJson);
        AssertEx.Equal((long)Encoding.UTF8.GetByteCount(envelope), fixture.Row().OutputBytes,
            "OutputBytes is the PLAINTEXT UTF-8 length of the composed envelope. A regression to a ciphertext column length fails on the first row.");
    }

    [Test]
    public async Task NothingIsPublishedBeforeTheRowCommits()
    {
        using var fixture = new Fixture();
        using var scope = AgentRunConversationContext.BeginScope(fixture.ConversationId);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Executions.BlockOutputAppendUntil = release;

        var call = fixture.Handler.ExecuteAsync(Arguments());
        await fixture.Buffer.WaitForReserveAsync().ConfigureAwait(false);

        AssertEx.Equal(expected: 1, fixture.Buffer.Reserved.Count);
        AssertEx.Empty(fixture.Buffer.Published, "A frame published before its row commits could name a result absent from durable history.");

        release.SetResult();
        _ = await call.ConfigureAwait(false);
        AssertEx.Equal(expected: 1, fixture.Buffer.Published.Count, "Publish arrives only after the commit returns.");
    }

    [Test]
    public async Task WhenTheReserveRefuses_NothingIsPublishedAndTheSequenceIsAbandoned()
    {
        // The store's in-transaction reserve refusing is unreachable through the pre-check on a healthy node; it is the
        // defence-in-depth half, and it must still resolve its reservation.
        using var fixture = new Fixture(maxOutputBytesPerExecution: 40, preCheckCap: 1_000_000);
        using var scope = AgentRunConversationContext.BeginScope(fixture.ConversationId);

        var answer = await fixture.Handler.ExecuteAsync(Arguments()).ConfigureAwait(false);

        AssertEx.Contains(answer, "nothing further was delivered");
        AssertEx.Empty(fixture.Executions.Events);
        AssertEx.Empty(fixture.Buffer.Published);
        AssertEx.Equal(expected: 1, fixture.Buffer.Reserved.Count);
        AssertEx.Equal(fixture.Buffer.Reserved[0], fixture.Buffer.Abandoned.Single(), "The reservation Reserve returned is exactly the one abandoned.");
    }

    [Test]
    public async Task WhenPersistenceThrows_NothingIsPublishedAndTheReservationIsAbandonedBeforeTheThrowEscapes()
    {
        // The tool THROWS on purpose here: that is what makes an unbacked frame unrepeatable — the run terminalizes
        // Failed / internal-failure and there is no next call that could publish a second one.
        using var fixture = new Fixture();
        using var scope = AgentRunConversationContext.BeginScope(fixture.ConversationId);
        fixture.Executions.ThrowOnNextOutputAppend = true;

        _ = await AssertEx.ThrowsAsync<Exception>(async () => await fixture.Handler.ExecuteAsync(Arguments()).ConfigureAwait(false),
            "A persistence failure must end the turn rather than return a sentence.").ConfigureAwait(false);

        AssertEx.Empty(fixture.Buffer.Published);
        AssertEx.Equal(fixture.Buffer.Reserved.Single(), fixture.Buffer.Abandoned.Single(),
            "Abandon runs BEFORE the exception escapes, or the caller's stream sits at this sequence for the life of the entry.");
    }

    [Test]
    public async Task EveryReserveIsFollowedByExactlyOnePublishOrAbandon()
    {
        // The invariant across all four paths: success, a refused reserve, a persistence throw, and the over-cap
        // pre-check (which takes no reservation at all and therefore contributes to neither side).
        using var fixture = new Fixture(maxOutputBytesPerExecution: 200);
        using var scope = AgentRunConversationContext.BeginScope(fixture.ConversationId);

        _ = await fixture.Handler.ExecuteAsync(Arguments("""{"a":1}""")).ConfigureAwait(false);

        fixture.Executions.ThrowOnNextOutputAppend = true;
        _ = await AssertEx.ThrowsAsync<Exception>(async () => await fixture.Handler.ExecuteAsync(Arguments("""{"b":2}""")).ConfigureAwait(false)).ConfigureAwait(false);

        _ = await fixture.Handler.ExecuteAsync(Arguments($$"""{"c":"{{new string('z', count: 400)}}"}""")).ConfigureAwait(false);

        AssertEx.Equal(fixture.Buffer.Reserved.Count,
            fixture.Buffer.Published.Count + fixture.Buffer.Abandoned.Count,
            "Every reservation is resolved exactly once, and only a reservation that was taken is resolved at all.");
    }

    [Test]
    public async Task AnAbandonedHoleDoesNotStopTheNextCallFromPublishing()
    {
        using var fixture = new Fixture();
        using var scope = AgentRunConversationContext.BeginScope(fixture.ConversationId);
        fixture.Executions.ThrowOnNextOutputAppend = true;
        _ = await AssertEx.ThrowsAsync<Exception>(async () => await fixture.Handler.ExecuteAsync(Arguments()).ConfigureAwait(false)).ConfigureAwait(false);

        var answer = await fixture.Handler.ExecuteAsync(Arguments()).ConfigureAwait(false);

        AssertEx.Contains(answer, "Output delivered");
        AssertEx.Equal(expected: 1, fixture.Buffer.Published.Count);
        AssertEx.True(fixture.Buffer.Published[0].Sequence > fixture.Buffer.Abandoned[0],
            "The abandoned sequence stays a permanent hole, and the next call takes the sequence after it.");
    }

    [Test]
    public async Task TheSequenceComesFromTheBufferReservation_NotTheRow()
    {
        // The buffer mints for EVERY event including assistant.delta, which is never persisted, so the row's
        // LastSequence necessarily lags the ring. A DB-allocated number would collide with one already streamed.
        using var fixture = new Fixture(bufferWatermark: 40);
        using var scope = AgentRunConversationContext.BeginScope(fixture.ConversationId);

        _ = await fixture.Handler.ExecuteAsync(Arguments()).ConfigureAwait(false);

        AssertEx.Equal(expected: 41L, fixture.Executions.Events[0].Sequence);
        AssertEx.Equal(expected: 41L, fixture.Buffer.Published[0].Sequence);
    }

    [Test]
    public async Task LastSequenceNeverGoesBackwards()
    {
        using var fixture = new Fixture(rowLastSequence: 40, bufferWatermark: 11);
        using var scope = AgentRunConversationContext.BeginScope(fixture.ConversationId);

        _ = await fixture.Handler.ExecuteAsync(Arguments()).ConfigureAwait(false);

        AssertEx.Equal(expected: 40L, fixture.Row().LastSequence, "The row's watermark is a running MAXIMUM, so a slower appender cannot move it back.");
    }

    [Test]
    [Arguments("not-a-media-type")]
    [Arguments("application/")]
    [Arguments("APPLICATION/JSON")]
    public async Task InvalidContentType_ReturnsAnErrorAndEmitsNothing(string contentType)
    {
        using var fixture = new Fixture();
        using var scope = AgentRunConversationContext.BeginScope(fixture.ConversationId);

        var answer = await fixture.Handler.ExecuteAsync(Arguments("""{"a":1}""", contentType)).ConfigureAwait(false);

        AssertEx.Contains(answer, "is not a media type");
        AssertEx.Empty(fixture.Executions.Events);
        AssertEx.Empty(fixture.Buffer.Reserved);
    }

    [Test]
    public async Task MalformedArguments_ReturnASentenceNotAThrow()
    {
        using var fixture = new Fixture();
        using var scope = AgentRunConversationContext.BeginScope(fixture.ConversationId);

        var unknownKey = await fixture.Handler.ExecuteAsync("""{"payload":{"a":1},"nope":true}""").ConfigureAwait(false);
        var noPayload = await fixture.Handler.ExecuteAsync("""{"contentType":"application/json"}""").ConfigureAwait(false);

        AssertEx.Contains(unknownKey, "Send exactly this shape");
        AssertEx.Contains(noPayload, "needs a payload");
        AssertEx.Empty(fixture.Executions.Events);
    }

    [Test]
    public async Task MalformedArguments_DoNotPutTheModelsPropertyNameInTheLog()
    {
        // UnmappedMemberHandling.Disallow makes the parser quote the unexpected PROPERTY NAME, and that name is
        // model-produced: a prompt can steer generated text into it. Attaching the exception carried that straight
        // into the debug log, against the no prompt/request/response content rule.
        using var fixture = new Fixture();
        using var scope = AgentRunConversationContext.BeginScope(fixture.ConversationId);

        var answer = await fixture.Handler.ExecuteAsync("""{"payload":{"a":1},"customer-secret-launch-code":true}""").ConfigureAwait(false);

        AssertEx.Contains(answer, "Send exactly this shape");
        AssertEx.Empty(fixture.Logger.Entries.Where(entry => entry.Message.Contains("customer-secret-launch-code", StringComparison.Ordinal)
                                                             || entry.Exception?.Message.Contains("customer-secret-launch-code", StringComparison.Ordinal) == true));
        AssertEx.True(fixture.Logger.HasEntry(LogLevel.Debug, "could not read its arguments"),
            "The refusal is still reported, with the execution id an operator can act on.");
        AssertEx.Empty(fixture.Logger.Entries.Where(static entry => entry.Exception is not null),
            "No parser exception may be attached to this entry at all: its message is what carries the property name.");
    }

    [Test]
    public async Task TheAcknowledgementDoesNotContainThePayload()
    {
        using var fixture = new Fixture();
        using var scope = AgentRunConversationContext.BeginScope(fixture.ConversationId);

        var answer = await fixture.Handler.ExecuteAsync(Arguments("""{"secret":"launch-code-alpha-zero"}""")).ConfigureAwait(false);

        AssertEx.False(answer.Contains("launch-code-alpha-zero", StringComparison.Ordinal), "The acknowledgement must never echo the payload back into the transcript.");
    }

    [Test]
    public async Task WhenTheBufferEntryIsGone_ReturnsASentenceRatherThanThrowingOntoTheRunnerThread()
    {
        // The post-terminal removal race: the row still read Running a moment ago, and Reserve now finds no entry. Every
        // other refusal on this path is a sentence, and a throw here would end the turn instead.
        using var fixture = new Fixture();
        using var scope = AgentRunConversationContext.BeginScope(fixture.ConversationId);
        _ = fixture.Buffer.Untracked.Add(fixture.ExecutionId);

        var answer = await fixture.Handler.ExecuteAsync(Arguments()).ConfigureAwait(false);

        AssertEx.Contains(answer, "No integration execution is currently running");
        AssertEx.Empty(fixture.Executions.Events);
        AssertEx.Empty(fixture.Buffer.Published);
        AssertEx.Empty(fixture.Buffer.Abandoned, "Reserve returned no sequence, so there is nothing to abandon.");
    }

    private static string Arguments(string payload = """{"ok":true}""", string? contentType = null) =>
        contentType is null
            ? $$"""{"payload":{{payload}}}"""
            : $$"""{"contentType":"{{contentType}}","payload":{{payload}}}""";

    private sealed class Fixture : IDisposable
    {
        private readonly ServiceProvider _provider;

        public Fixture(bool runningExecution = true,
            int maxOutputBytes = 262_144,
            int maxOutputBytesPerExecution = 1_048_576,
            int? preCheckCap = null,
            long bufferWatermark = 0,
            long rowLastSequence = 1)
        {
            ConversationId = Guid.NewGuid();
            var sessionId = Guid.NewGuid();
            var triggerId = Guid.NewGuid();
            ExecutionId = Guid.NewGuid();

            _ = Sessions.Seed(sessionId, triggerId, ConversationId, Guid.NewGuid());
            _ = Executions.Seed(ExecutionId,
                triggerId,
                sessionId,
                runningExecution ? IntegrationExecutionStatus.Running : IntegrationExecutionStatus.Completed,
                lastSequence: rowLastSequence);

            Buffer = new RecordingBuffer(bufferWatermark);

            var services = new ServiceCollection();
            services.AddSingleton<IIntegrationSessionStore>(Sessions);
            services.AddSingleton<IIntegrationExecutionStore>(Executions);
            _provider = services.BuildServiceProvider();

            // preCheckCap raises the HANDLER's aggregate ceiling above the STORE's, which is the only way to drive the
            // store's in-transaction refusal — on a healthy node the pre-check gets there first.
            Handler = new EmitOutputToolHandler(_provider.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new IntegrationOptions
                {
                    MaxOutputBytes = maxOutputBytes,
                    MaxOutputBytesPerExecution = preCheckCap ?? maxOutputBytesPerExecution
                }),
                Buffer,
                TimeProvider.System,
                Logger);

            StoreCap = maxOutputBytesPerExecution;
            Executions.OutputCapOverride = preCheckCap is null ? null : maxOutputBytesPerExecution;
        }

        public Guid ConversationId { get; }

        public Guid ExecutionId { get; }

        public long StoreCap { get; }

        public FakeIntegrationSessionStore Sessions { get; } = new();

        public FakeIntegrationExecutionStore Executions { get; } = new();

        public RecordingBuffer Buffer { get; }

        /// <summary>The handler's own log, so a suite can assert what a refusal did and did NOT write.</summary>
        public RecordingLogger<EmitOutputToolHandler> Logger { get; } = new();

        public EmitOutputToolHandler Handler { get; }

        public IntegrationExecutionSnapshot Row() =>
            Executions.Rows.Single(row => row.Id == ExecutionId);

        public void Dispose() =>
            _provider.Dispose();
    }
}

namespace XE_Local_AI_Engine.Tests.Benchmarks;

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class BenchmarkExecutionPrimitivesTests
{
    [Test]
    public async Task ContextAdmission_RejectsUnknownAndInsufficientContext_AndRecordsEffectiveValue()
    {
        var policy = new BenchmarkContextAdmissionPolicy(8192);

        var unknown = await policy.EvaluateAsync(Context(effective: null));
        var insufficient = await policy.EvaluateAsync(Context(effective: 4096));

        AssertEx.False(unknown.IsAllowed);
        AssertEx.Equal(InvocationGenerationAdmissionReasonCodes.EffectiveContextUnavailable, unknown.RejectionReasonCode);
        AssertEx.False(insufficient.IsAllowed);
        AssertEx.Equal(InvocationGenerationAdmissionReasonCodes.EffectiveContextInsufficient, insufficient.RejectionReasonCode);
        AssertEx.Equal<int?>(4096, policy.EffectiveContextTokens);
    }

    [Test]
    public async Task ContextAdmission_AllowsExactRequiredContext()
    {
        var policy = new BenchmarkContextAdmissionPolicy(8192);

        var decision = await policy.EvaluateAsync(Context(effective: 8192));

        AssertEx.True(decision.IsAllowed);
        AssertEx.Equal<int?>(8192, policy.EffectiveContextTokens);
    }

    [Test]
    public void EventBuffer_TrimsByCountAndReturnsResetForCursorBeforeRetainedHistory()
    {
        var runId = Guid.NewGuid();
        var buffer = Buffer(maxEvents: 2, maxBytes: 4096);
        _ = buffer.Append(runId, BenchmarkRunStreamEventKind.OutputDelta, new BenchmarkRunStreamPayload(Content: "one"));
        _ = buffer.Append(runId, BenchmarkRunStreamEventKind.OutputDelta, new BenchmarkRunStreamPayload(Content: "two"));
        _ = buffer.Append(runId, BenchmarkRunStreamEventKind.OutputDelta, new BenchmarkRunStreamPayload(Content: "three"));

        var reset = buffer.Replay(runId, afterSequence: 0, runVersion: 7);
        var retained = buffer.Replay(runId, afterSequence: 1, runVersion: 7);

        AssertEx.True(reset.ResetRequired);
        AssertEx.Equal(3L, reset.LatestSequence);
        AssertEx.False(retained.ResetRequired);
        AssertEx.True(retained.Events.Select(static item => item.Sequence).SequenceEqual([2L, 3L]));
    }

    [Test]
    public void EventBuffer_DeduplicatesReservedSequenceAndEvictsPlaintextOnTerminal()
    {
        var runId = Guid.NewGuid();
        var buffer = Buffer(maxEvents: 8, maxBytes: 4096);
        var published = 0;
        buffer.EventPublished += (_, _) => published++;
        var streamEvent = buffer.Reserve(runId,
            BenchmarkRunStreamEventKind.OutputDelta,
            new BenchmarkRunStreamPayload(Content: "sensitive"));

        buffer.PublishReserved(streamEvent);
        buffer.PublishReserved(streamEvent);
        buffer.EvictPlaintext(runId);
        var replay = buffer.Replay(runId, streamEvent.Sequence, runVersion: 9);

        AssertEx.Equal(1, published);
        AssertEx.True(replay.ResetRequired);
        AssertEx.Empty(replay.Events);
        AssertEx.Equal(streamEvent.Sequence, replay.LatestSequence);
    }

    [Test]
    public void EventBuffer_JudgePhaseReopensReplayAfterPrimaryPlaintextEviction()
    {
        var runId = Guid.NewGuid();
        var buffer = Buffer(maxEvents: 8, maxBytes: 4096);
        _ = buffer.Append(runId, BenchmarkRunStreamEventKind.OutputDelta, new BenchmarkRunStreamPayload(Content: "primary"));
        var primaryTerminal = buffer.Append(runId,
            BenchmarkRunStreamEventKind.TerminalSnapshotAvailable,
            new BenchmarkRunStreamPayload(State: BenchmarkPrimaryStatus.Succeeded.ToString()));
        buffer.EvictPlaintext(runId);

        buffer.BeginActivePhase(runId, primaryTerminal.Sequence);
        var judgeRunning = buffer.Append(runId,
            BenchmarkRunStreamEventKind.JudgeState,
            new BenchmarkRunStreamPayload(State: BenchmarkJudgeStatus.Running.ToString()));

        var current = buffer.Replay(runId, primaryTerminal.Sequence, runVersion: 3);
        var stale = buffer.Replay(runId, primaryTerminal.Sequence - 1, runVersion: 3);
        AssertEx.False(current.ResetRequired);
        AssertEx.Equal(1, current.Events.Count);
        AssertEx.Equal(judgeRunning.Sequence, current.Events[0].Sequence);
        AssertEx.True(stale.ResetRequired);

        var judgeTerminal = buffer.Append(runId,
            BenchmarkRunStreamEventKind.TerminalSnapshotAvailable,
            new BenchmarkRunStreamPayload(State: BenchmarkJudgeStatus.Succeeded.ToString()));
        buffer.EvictPlaintext(runId);
        var terminalReplay = buffer.Replay(runId, judgeTerminal.Sequence, runVersion: 4);
        AssertEx.True(terminalReplay.ResetRequired);
        AssertEx.Empty(terminalReplay.Events);
        AssertEx.Equal(judgeTerminal.Sequence, terminalReplay.LatestSequence);
    }

    [Test]
    public void EventBuffer_TrimsSingleOversizedUtf8Payload()
    {
        var runId = Guid.NewGuid();
        var buffer = Buffer(maxEvents: 8, maxBytes: 64);

        var streamEvent = buffer.Append(runId,
            BenchmarkRunStreamEventKind.OutputDelta,
            new BenchmarkRunStreamPayload(Content: new string('\u20ac', 128)));
        var replay = buffer.Replay(runId, afterSequence: 0, runVersion: 1);

        AssertEx.True(replay.ResetRequired);
        AssertEx.Empty(replay.Events);
        AssertEx.Equal(streamEvent.Sequence, replay.LatestSequence);
    }

    [Test]
    public void CancellationRegistry_OwnsOneRegistrationAndSignalsOnlyMatchingWork()
    {
        var registry = new BenchmarkCancellationRegistry();
        var runId = Guid.NewGuid();
        using var primary = registry.Register(runId, BenchmarkWorkKind.Primary, CancellationToken.None);
        using var judge = registry.Register(runId, BenchmarkWorkKind.Judge, CancellationToken.None);

        var signalled = registry.TryCancel(runId, BenchmarkWorkKind.Judge);

        AssertEx.True(signalled);
        AssertEx.False(primary.Token.IsCancellationRequested);
        AssertEx.True(judge.Token.IsCancellationRequested);
        _ = AssertEx.Throws<InvalidOperationException>(() => registry.Register(runId, BenchmarkWorkKind.Primary, CancellationToken.None));
    }

    [Test]
    public async Task CancellationService_RunningPrimaryPersistsRequestThenSignalsOwnedToken()
    {
        var run = Run(BenchmarkPrimaryStatus.Running, BenchmarkJudgeStatus.Pending, version: 4);
        var requested = run with
        {
            PrimaryStatus = BenchmarkPrimaryStatus.CancelRequested,
            Version = 5
        };
        var store = Substitute.For<IBenchmarkStore>();
        store.GetRunAsync(run.Id, Arg.Any<CancellationToken>()).Returns(run);
        store.CancelAsync(run.Id, run.Version, Arg.Any<CancellationToken>()).Returns(requested);
        var registry = new BenchmarkCancellationRegistry();
        using var registration = registry.Register(run.Id, BenchmarkWorkKind.Primary, CancellationToken.None);
        var service = new BenchmarkCancellationService(store, registry);

        var result = await service.CancelAsync(run.Id, run.Version, BenchmarkCancellationTarget.Primary);

        AssertEx.Equal(BenchmarkPrimaryStatus.CancelRequested, result.PrimaryStatus);
        AssertEx.True(registration.Token.IsCancellationRequested);
        _ = store.Received(1).CancelAsync(run.Id, run.Version, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CancellationService_RejectsJudgeCancellationWhilePrimaryIsRunning()
    {
        var run = Run(BenchmarkPrimaryStatus.Running, BenchmarkJudgeStatus.Pending, version: 4);
        var store = Substitute.For<IBenchmarkStore>();
        store.GetRunAsync(run.Id, Arg.Any<CancellationToken>()).Returns(run);
        var service = new BenchmarkCancellationService(store, new BenchmarkCancellationRegistry());

        var exception = await AssertEx.ThrowsAsync<BenchmarkConflictException>(() =>
            service.CancelAsync(run.Id, run.Version, BenchmarkCancellationTarget.Judge));

        AssertEx.Equal("JudgeNotCancellable", exception.Code);
        _ = store.DidNotReceive().CancelAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public void JudgeResultParser_AcceptsOnlyExactVersionedSchema()
    {
        var fingerprint = $"v1:{new string('b', 64)}";

        var result = BenchmarkJudgeExecutor.ParseResult("{\"schemaVersion\":1,\"score\":4,\"rationale\":\"solid\"}", fingerprint, promptVersion: 1);
        _ = AssertEx.Throws<InvalidOperationException>(() => BenchmarkJudgeExecutor.ParseResult(
            "{\"schemaVersion\":1,\"score\":4,\"rationale\":\"solid\",\"extra\":true}", fingerprint, promptVersion: 1));
        _ = AssertEx.Throws<InvalidOperationException>(() => BenchmarkJudgeExecutor.ParseResult(
            "```json {\"schemaVersion\":1,\"score\":4,\"rationale\":\"solid\"} ```", fingerprint, promptVersion: 1));

        AssertEx.Equal(1, result.SchemaVersion);
        AssertEx.Equal(4, result.Score);
        AssertEx.Equal("solid", result.Rationale);
        AssertEx.Equal(fingerprint, result.JudgeModelContentFingerprint);
    }

    [Test]
    public void JudgeResultParser_AcceptsAResponseTheConstrainedSchemaCanProduce()
    {
        // The response-format schema and the strict parser have to agree, or constraining the decode buys nothing.
        // Anything the schema admits — no extra properties, an integer score in 1..5, a string rationale — must parse.
        var fingerprint = $"v1:{new string('b', 64)}";

        var minimum = BenchmarkJudgeExecutor.ParseResult("{\"schemaVersion\":1,\"score\":1,\"rationale\":\"weak\"}", fingerprint, promptVersion: 1);
        var maximum = BenchmarkJudgeExecutor.ParseResult("{\"rationale\":\"excellent\",\"score\":5,\"schemaVersion\":1}", fingerprint, promptVersion: 1);

        AssertEx.Equal(1, minimum.Score);
        AssertEx.Equal(5, maximum.Score);
        AssertEx.Equal("excellent", maximum.Rationale);
    }

    [Test]
    public void JudgeResponseFormatSchema_IsTheFrozenContractWithoutTheGrammarRepetitionBounds()
    {
        // llama-server compiles the response format into GBNF, where minLength/maxLength are repetition keywords with a
        // hard ceiling. The frozen schema keeps them (the prompt states them and ParseResult enforces them); the
        // constrained-decoding copy must not, or the sampler can fail to initialise and the turn never runs at all.
        using var responseFormat = JsonDocument.Parse(BenchmarkFrozenPolicies.JudgeResponseFormatSchemaJson);
        using var frozen = JsonDocument.Parse(BenchmarkFrozenPolicies.JudgeOutputSchemaJson);

        var rationale = responseFormat.RootElement.GetProperty("properties").GetProperty("rationale");
        AssertEx.False(rationale.TryGetProperty("maxLength", out _), "maxLength is a GBNF repetition bound.");
        AssertEx.False(rationale.TryGetProperty("minLength", out _), "minLength is a GBNF repetition bound.");
        AssertEx.True(frozen.RootElement.GetProperty("properties").GetProperty("rationale").TryGetProperty("maxLength", out _),
            "The FROZEN schema keeps its bounds — it is hashed into every snapshot and must not change.");

        // Everything else is the same contract: the same required set, and the numeric score range survives (a range is
        // not a repetition), so decoding can never be constrained into a score the parser then rejects.
        var required = responseFormat.RootElement.GetProperty("required").EnumerateArray().Select(static value => value.GetString()).ToArray();
        AssertEx.Equal(3, required.Length);
        foreach (var name in new[] { "schemaVersion", "score", "rationale" })
        {
            AssertEx.Contains(required, name);
        }

        var score = responseFormat.RootElement.GetProperty("properties").GetProperty("score");
        AssertEx.Equal(1, score.GetProperty("minimum").GetInt32());
        AssertEx.Equal(5, score.GetProperty("maximum").GetInt32());
        AssertEx.False(responseFormat.RootElement.GetProperty("additionalProperties").GetBoolean());
    }

    [Test]
    public void JudgeSerialization_RoundTripsThroughTheWritersOwnOptions_AndDefaultOptionsWouldZeroIt()
    {
        // The live bug: the writer uses JsonSerializerDefaults.Web (camelCase) and the endpoint mapper deserialized with
        // DEFAULT (PascalCase) options, so every property bound to its default and the API returned score 0 with a null
        // rationale — a shape the frontend's schema rejects, taking the whole run detail down with it.
        var written = BenchmarkExecutionSerialization.SerializeJudge(new BenchmarkJudgeResultV1(1, 4, "solid", $"v1:{new string('b', 64)}", 1));

        AssertEx.Contains(Encoding.UTF8.GetString(written), "\"schemaVersion\":1");

        var roundTripped = AssertEx.NotNull(BenchmarkExecutionSerialization.DeserializeJudge(written));
        AssertEx.Equal(1, roundTripped.SchemaVersion);
        AssertEx.Equal(4, roundTripped.Score);
        AssertEx.Equal("solid", roundTripped.Rationale);
        AssertEx.Equal(1, roundTripped.PromptVersion);

        var withDefaultOptions = AssertEx.NotNull(JsonSerializer.Deserialize<BenchmarkJudgeResultV1>(written));
        AssertEx.Equal(0, withDefaultOptions.Score,
            "If this ever binds, camelCase stopped being the stored shape — revisit the pin rather than deleting it.");
    }

    [Test]
    [Arguments("")]
    [Arguments("not json")]
    public void JudgeSerialization_ForAnAbsentOrUnreadablePayload_IsNullRatherThanAThrow(string payload)
    {
        AssertEx.Null(BenchmarkExecutionSerialization.DeserializeJudge(Encoding.UTF8.GetBytes(payload)));
        AssertEx.Null(BenchmarkExecutionSerialization.DeserializeJudge(null));
    }

    private static InvocationGenerationAdmissionContext Context(int? effective) =>
        new()
        {
            InvocationId = Guid.NewGuid(),
            RequestedContextTokens = 8192,
            EffectiveContextTokens = effective,
            ModelId = "model.gguf",
            ProviderName = "llamacpp"
        };

    private static BenchmarkEventBuffer Buffer(int maxEvents, int maxBytes) =>
        new(Options.Create(new BenchmarkEventBufferOptions
        {
            MaxEventCount = maxEvents,
            MaxUtf8Bytes = maxBytes
        }));

    private static BenchmarkRunRecord Run(BenchmarkPrimaryStatus primary, BenchmarkJudgeStatus judge, long version) =>
        new(Guid.NewGuid(), Guid.NewGuid(), new byte[]
            {
                1
            }, "model.gguf", null, $"v1:{new string('a', 64)}", "Agent", 1, 8192,
            primary, null, null, null, null, null, 0, null, judge, null, null, null, version, 1, 1, null, null, null, 1);
}

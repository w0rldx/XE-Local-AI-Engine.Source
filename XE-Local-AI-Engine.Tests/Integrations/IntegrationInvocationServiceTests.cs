namespace XE_Local_AI_Engine.Tests.Integrations;

using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Integrations;
using XE_Local_AI_Engine.Client.Services.Integrations.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The accept path. Three properties carry the design: every rejection is indistinguishable from the others a
///     caller could probe with, admission commits BEFORE anything else is written so a rejected request leaves nothing
///     durable behind, and the sequence that reaches the row is the one the buffer minted.
/// </summary>
public sealed class IntegrationInvocationServiceTests
{
    [Test]
    public async Task Accept_WithAValidRequest_AdmitsCommitsAndEnqueuesInThatOrder()
    {
        var harness = new Harness();
        var trigger = harness.SeedTrigger("sensor-feed");

        var result = await harness.AcceptAsync(trigger.Name).ConfigureAwait(false);

        AssertEx.Equal(IntegrationAcceptOutcome.Accepted, result.Outcome);
        AssertEx.True(result.ExecutionId is not null && result.SessionId is not null, "An accepted request must name the row it created.");
        var executionId = result.ExecutionId!.Value;
        AssertEx.Equal(IntegrationExecutionStatus.Accepted, result.Status!.Value);
        AssertEx.Equal(expected: 1, harness.Executions.Rows.Count);
        AssertEx.Equal(expected: 1, harness.Executions.CreatedSessions.Count);
        AssertEx.True(harness.Queue.Reader.TryRead(out var queued) && queued == executionId, "The admitted execution must reach the coordinator's queue.");

        // R4-1's order: the durable rows come first, the owned conversation and the seed afterwards, at the id the
        // session row already recorded.
        Received.InOrder(() =>
        {
            _ = harness.Persistence.CreateConversationAsync(Arg.Any<NodeChatCreateConversationRequest>(), Arg.Any<CancellationToken>());
            _ = harness.Persistence.PersistUserMessageAsync(Arg.Any<NodeChatPersistUserMessageRequest>(), Arg.Any<CancellationToken>());
        });
        AssertEx.Equal(harness.Executions.CreatedSessions.Single().ConversationId, harness.CapturedConversation().ConversationId ?? Guid.Empty);
        AssertEx.Equal(NodeConversationKind.Integration, harness.CapturedConversation().Kind);
        AssertEx.Equal(executionId, harness.CapturedSeed().MessageId, "The seed message id IS the execution id, so a continuation needs no lookup table.");
    }

    [Test]
    public async Task Accept_CarriesTheBufferMintedSequenceIntoTheAcceptedEventRow()
    {
        var harness = new Harness();
        var trigger = harness.SeedTrigger("sensor-feed");

        var result = await harness.AcceptAsync(trigger.Name).ConfigureAwait(false);

        AssertEx.True(result.ExecutionId is not null);
        var executionId = result.ExecutionId!.Value;
        var accepted = harness.Executions.Events.Single(row => row.ExecutionId == executionId);
        AssertEx.Equal(IntegrationStreamEventTypes.ExecutionAccepted, accepted.EventType);
        AssertEx.Equal(expected: 1L, accepted.Sequence, "The accepted event is always sequence 1, minted by the buffer and by nothing else.");
        AssertEx.Equal(harness.Buffer.LastSequence(executionId), accepted.Sequence);
        AssertEx.Null(accepted.DetailJson, "The accepted event carries no payload; the store refuses one.");
    }

    [Test]
    [Arguments("no-such-trigger")]
    [Arguments("disabled-trigger")]
    [Arguments("not-allowlisted")]
    public async Task Accept_ForAnUnknownDisabledOrUnallowlistedTrigger_IsOneIndistinguishableRejection(string scenario)
    {
        // THE load-bearing masking assertion: a distinct code for "exists but not yours" would confirm the name to a
        // key that is explicitly scoped away from it.
        var harness = new Harness();
        _ = harness.SeedTrigger("disabled-trigger", enabled: false);
        var other = harness.SeedTrigger("not-allowlisted");
        var allowed = harness.SeedTrigger("allowed");
        harness.RestrictKeyTo(allowed.Id);

        var result = await harness.AcceptAsync(scenario).ConfigureAwait(false);

        AssertEx.Equal(IntegrationAcceptOutcome.TriggerNotFound, result.Outcome);
        AssertEx.Equal("No such trigger.", result.Message);
        AssertEx.Null(result.ExecutionId);
        AssertEx.Empty(harness.Executions.Rows);
        AssertEx.NotEqual(other.Id, Guid.Empty);
    }

    [Test]
    public async Task Accept_WithANullAllowlist_ReachesEveryTrigger()
    {
        var harness = new Harness();
        var trigger = harness.SeedTrigger("sensor-feed");

        AssertEx.Equal(IntegrationAcceptOutcome.Accepted, (await harness.AcceptAsync(trigger.Name).ConfigureAwait(false)).Outcome);
    }

    [Test]
    public async Task Accept_WithARevokedKey_AnswersTheGenericUnauthorizedAndWritesNothing()
    {
        var harness = new Harness();
        var trigger = harness.SeedTrigger("sensor-feed");
        harness.RevokeKey();

        var result = await harness.AcceptAsync(trigger.Name).ConfigureAwait(false);

        AssertEx.Equal(IntegrationAcceptOutcome.Unauthorized, result.Outcome);
        AssertEx.Empty(harness.Executions.Rows);
    }

    [Test]
    public async Task Accept_WhenTheKeyIsRevokedInsideTheAdmissionTransaction_IsStillUnauthorizedAndWritesNothing()
    {
        // The window the round-four ruling closed: a caller holds an authenticated request open, is revoked, and would
        // otherwise still create durable work. The transaction re-reads the row, so it cannot.
        var harness = new Harness();
        var trigger = harness.SeedTrigger("sensor-feed");
        harness.Executions.RevokedKeyPrefixes.Add(Harness.KeyPrefix);

        var result = await harness.AcceptAsync(trigger.Name).ConfigureAwait(false);

        AssertEx.Equal(IntegrationAcceptOutcome.Unauthorized, result.Outcome);
        AssertEx.Empty(harness.Executions.Rows);
        AssertEx.Empty(harness.Executions.CreatedSessions);
        _ = harness.Persistence.DidNotReceiveWithAnyArgs().CreateConversationAsync(default!, default);
    }

    [Test]
    public async Task Accept_WithNoInputsOrAnUnacceptedKind_IsRejectedWithoutWriting()
    {
        var harness = new Harness();
        var textOnly = harness.SeedTrigger("text-only", acceptedInputKinds: IntegrationInputKinds.Text);

        var empty = await harness.AcceptAsync(textOnly.Name, inputs: []).ConfigureAwait(false);
        var wrongKind = await harness.AcceptAsync(textOnly.Name, inputs: [Json("""{"a":1}""")]).ConfigureAwait(false);
        var blank = await harness.AcceptAsync(textOnly.Name, inputs: [Text("   ")]).ConfigureAwait(false);

        AssertEx.Equal(IntegrationAcceptOutcome.InputsRejected, empty.Outcome);
        AssertEx.Equal(IntegrationAcceptOutcome.InputsRejected, wrongKind.Outcome);
        AssertEx.Equal(IntegrationAcceptOutcome.InputsRejected, blank.Outcome);
        AssertEx.Empty(harness.Executions.Rows);
    }

    [Test]
    public async Task Accept_WithASeedPastTheCeiling_IsRejectedRatherThanSilentlyTruncated()
    {
        var harness = new Harness(maxSeedBytes: 64);
        var trigger = harness.SeedTrigger("sensor-feed");

        var result = await harness.AcceptAsync(trigger.Name, inputs: [Text(new string('x', count: 512))]).ConfigureAwait(false);

        AssertEx.Equal(IntegrationAcceptOutcome.InputsRejected, result.Outcome);
        AssertEx.Empty(harness.Executions.Rows);
    }

    [Test]
    public async Task Accept_ForACallerManagedTriggerOrASuppliedSessionId_IsUnsupported()
    {
        var harness = new Harness();
        var callerManaged = harness.SeedTrigger("caller-managed", sessionPolicy: IntegrationSessionPolicy.CallerManaged);
        var perInvocation = harness.SeedTrigger("per-invocation");

        var policy = await harness.AcceptAsync(callerManaged.Name).ConfigureAwait(false);
        var withSession = await harness.AcceptAsync(perInvocation.Name, sessionId: Guid.NewGuid()).ConfigureAwait(false);

        AssertEx.Equal(IntegrationAcceptOutcome.SessionUnsupported, policy.Outcome);
        AssertEx.Equal(IntegrationAcceptOutcome.SessionUnsupported, withSession.Outcome);
        AssertEx.Empty(harness.Executions.Rows);
    }

    [Test]
    public async Task Accept_WithTheSameRequestIdAndByteIdenticalBody_ReturnsTheFirstExecutionAndWritesNoSecondRow()
    {
        var harness = new Harness();
        var trigger = harness.SeedTrigger("sensor-feed");
        var requestId = Guid.NewGuid();
        var body = """{"requestId":"x","inputs":[]}"""u8.ToArray();

        var first = await harness.AcceptAsync(trigger.Name, requestId: requestId, rawBody: body).ConfigureAwait(false);
        var replay = await harness.AcceptAsync(trigger.Name, requestId: requestId, rawBody: body).ConfigureAwait(false);

        AssertEx.Equal(IntegrationAcceptOutcome.Duplicate, replay.Outcome);
        AssertEx.Equal(first.ExecutionId, replay.ExecutionId);
        AssertEx.Equal(expected: 1, harness.Executions.Rows.Count);
    }

    [Test]
    public async Task Accept_WithTheSameRequestIdAndABodyDifferingOnlyInWhitespace_Is409()
    {
        // Byte-exactness is the design, not a bug: there is no canonicalisation anywhere, so a retry must resend the
        // identical bytes. The API doc says so.
        var harness = new Harness();
        var trigger = harness.SeedTrigger("sensor-feed");
        var requestId = Guid.NewGuid();

        _ = await harness.AcceptAsync(trigger.Name, requestId: requestId, rawBody: """{"a":1}"""u8.ToArray()).ConfigureAwait(false);
        var conflict = await harness.AcceptAsync(trigger.Name, requestId: requestId, rawBody: """{"a": 1}"""u8.ToArray()).ConfigureAwait(false);

        AssertEx.Equal(IntegrationAcceptOutcome.RequestConflict, conflict.Outcome);
        AssertEx.Null(conflict.ExecutionId, "A conflict tells the caller nothing about the row it collided with.");
        AssertEx.Equal(expected: 1, harness.Executions.Rows.Count);
    }

    [Test]
    public async Task Accept_WhenAnotherPrincipalReplaysTheSameRequestIdAndBody_IsAdmittedAsItsOwnExecution()
    {
        // The uniqueness index is scoped to (PrincipalId, RequestId), so a foreign request id is simply NOT FOUND and
        // the request proceeds on its own merits — one integrator can never preclaim another's ids.
        var harness = new Harness(maxQueuedPerPrincipal: 4);
        var trigger = harness.SeedTrigger("sensor-feed");
        var requestId = Guid.NewGuid();
        var body = """{"a":1}"""u8.ToArray();
        var first = await harness.AcceptAsync(trigger.Name, requestId: requestId, rawBody: body).ConfigureAwait(false);

        var stranger = await harness.AcceptAsync(trigger.Name, requestId: requestId, rawBody: body, principalId: Guid.NewGuid()).ConfigureAwait(false);

        AssertEx.Equal(IntegrationAcceptOutcome.Accepted, stranger.Outcome);
        AssertEx.NotEqual(first.ExecutionId, stranger.ExecutionId);
        AssertEx.Equal(expected: 2, harness.Executions.Rows.Count);
    }

    [Test]
    public async Task Accept_WhenASecondCredentialOfTheSamePrincipalReplaysIt_IsADuplicate()
    {
        // A rotation must not strand an in-flight request: ownership and the fingerprint key on the principal, not on
        // which credential happened to be presented.
        var harness = new Harness();
        var trigger = harness.SeedTrigger("sensor-feed");
        var requestId = Guid.NewGuid();
        var body = """{"a":1}"""u8.ToArray();
        var first = await harness.AcceptAsync(trigger.Name, requestId: requestId, rawBody: body).ConfigureAwait(false);

        harness.RotateCredential();
        var replay = await harness.AcceptAsync(trigger.Name, requestId: requestId, rawBody: body, keyPrefix: Harness.RotatedKeyPrefix).ConfigureAwait(false);

        AssertEx.Equal(IntegrationAcceptOutcome.Duplicate, replay.Outcome);
        AssertEx.Equal(first.ExecutionId, replay.ExecutionId);
    }

    [Test]
    public async Task Accept_WhenAConcurrentAcceptWinsTheUniqueIndexRace_ResolvesItAsADuplicateRatherThanA500()
    {
        var harness = new Harness();
        var trigger = harness.SeedTrigger("sensor-feed");
        var requestId = Guid.NewGuid();
        var body = """{"a":1}"""u8.ToArray();
        var winner = await harness.AcceptAsync(trigger.Name, requestId: requestId, rawBody: body).ConfigureAwait(false);

        // The loser's pre-check ran before the winner committed; only the index can decide it.
        harness.Executions.HideNextRequestIdLookup = true;
        harness.Executions.FailNextAcceptWithUniqueViolation = true;
        var loser = await harness.AcceptAsync(trigger.Name, requestId: requestId, rawBody: body).ConfigureAwait(false);

        AssertEx.Equal(IntegrationAcceptOutcome.Duplicate, loser.Outcome);
        AssertEx.Equal(winner.ExecutionId, loser.ExecutionId);
    }

    [Test]
    public async Task Accept_WhenTheNodeWideCapIsFull_Is503AndWritesNothingMore()
    {
        var harness = new Harness(maxQueued: 2, maxQueuedPerPrincipal: 2);
        var trigger = harness.SeedTrigger("sensor-feed");
        _ = await harness.AcceptAsync(trigger.Name).ConfigureAwait(false);
        _ = await harness.AcceptAsync(trigger.Name).ConfigureAwait(false);

        var refused = await harness.AcceptAsync(trigger.Name).ConfigureAwait(false);

        AssertEx.Equal(IntegrationAcceptOutcome.QueueFull, refused.Outcome);
        AssertEx.Equal(expected: 2, harness.Executions.Rows.Count, "A refused admission writes nothing at all.");
    }

    [Test]
    public async Task Accept_WhenOnePrincipalIsAtItsOwnCap_RefusesItWhileAdmittingAnother()
    {
        // The fairness floor: one noisy integrator must not fill the node-wide queue and starve every other one.
        var harness = new Harness(maxQueued: 8, maxQueuedPerPrincipal: 1);
        var trigger = harness.SeedTrigger("sensor-feed");
        _ = await harness.AcceptAsync(trigger.Name).ConfigureAwait(false);

        var noisy = await harness.AcceptAsync(trigger.Name).ConfigureAwait(false);
        var other = await harness.AcceptAsync(trigger.Name, principalId: Guid.NewGuid()).ConfigureAwait(false);

        AssertEx.Equal(IntegrationAcceptOutcome.QueueFull, noisy.Outcome);
        AssertEx.Equal(IntegrationAcceptOutcome.Accepted, other.Outcome, "A different principal is admitted at the same moment, with node-wide slots free.");
    }

    [Test]
    public async Task Accept_ReleasesTheBufferReservationOnEveryRejectionPath()
    {
        // A leaked reservation holds a tracked slot that only a terminal event could free, and a rejected execution
        // will never get one — so the ring would fill with ghosts.
        var harness = new Harness(maxQueued: 1, maxQueuedPerPrincipal: 1, maxTracked: 3);
        var trigger = harness.SeedTrigger("sensor-feed");
        _ = await harness.AcceptAsync(trigger.Name).ConfigureAwait(false);

        for (var attempt = 0; attempt < 10; attempt++)
        {
            AssertEx.Equal(IntegrationAcceptOutcome.QueueFull, (await harness.AcceptAsync(trigger.Name).ConfigureAwait(false)).Outcome);
        }

        AssertEx.Equal(expected: 1, harness.Buffer.TrackedCount, "Only the admitted execution may still hold an entry.");
    }

    [Test]
    public async Task Accept_WhenTheQueueRefusesAnAdmittedRow_TerminalizesItQueueFullAndAnswers503()
    {
        // Defended, not expected: the admission count gates it. A discarded id would otherwise strand an Accepted row
        // that nothing drains and that the count blocks a slot with forever.
        var harness = new Harness(queueCapacity: 1);
        var trigger = harness.SeedTrigger("sensor-feed");
        _ = await harness.AcceptAsync(trigger.Name).ConfigureAwait(false);

        var refused = await harness.AcceptAsync(trigger.Name).ConfigureAwait(false);

        AssertEx.Equal(IntegrationAcceptOutcome.QueueFull, refused.Outcome);
        var stranded = harness.Executions.Rows.Single(static row => row.Status == IntegrationExecutionStatus.Failed);
        AssertEx.Equal(IntegrationFailureCategories.QueueFull, stranded.FailureCategory);
        AssertEx.Contains(harness.Executions.Events,
            row => row.ExecutionId == stranded.Id && row.EventType == IntegrationStreamEventTypes.ExecutionFailed,
            "The terminal status and its event are written in one transaction, so a reader can never wait forever for a completion that was never recorded.");
        AssertEx.False(harness.Buffer.IsTracked(stranded.Id), "Nothing will read it: the caller got a 503 and never learned an id.");
    }

    [Test]
    public async Task Accept_WhenTheConversationWriteFails_LeavesTheRowCommittedAndAcceptedAndQueuesItAnyway()
    {
        // Runs forward, never backward. The row is a visible, cancellable, audited Accepted row that the coordinator
        // terminalises with a real reason, which is strictly better than a silent rollback.
        var harness = new Harness();
        var trigger = harness.SeedTrigger("sensor-feed");
        harness.Persistence.CreateConversationAsync(Arg.Any<NodeChatCreateConversationRequest>(), Arg.Any<CancellationToken>())
               .ThrowsAsync(new InvalidOperationException("disk on fire"));

        _ = await AssertEx.ThrowsAsync<InvalidOperationException>(() => harness.AcceptAsync(trigger.Name)).ConfigureAwait(false);

        var row = harness.Executions.Rows.Single();
        AssertEx.Equal(IntegrationExecutionStatus.Accepted, row.Status);
        AssertEx.True(harness.Queue.Reader.TryRead(out var queued) && queued == row.Id, "The coordinator still has to see it, or it waits for the next restart sweep.");
        AssertEx.True(harness.Buffer.IsTracked(row.Id), "The buffer entry must survive to carry the terminal event the coordinator will write.");
    }

    private static IntegrationInputDto Text(string text) =>
        new(IntegrationInputKinds.Text, text, Label: null, Json: null);

    private static IntegrationInputDto Json(string json) =>
        new(IntegrationInputKinds.Json, Text: null, "payload", json);

    private sealed class Harness
    {
        public const string KeyPrefix = "xeint_aaaaaaaa";

        public const string RotatedKeyPrefix = "xeint_bbbbbbbb";

        private readonly List<IntegrationApiKeySnapshot> _keys;
        private readonly IIntegrationApiKeyStore _keyStore;

        public Harness(int maxQueued = 8,
            int maxQueuedPerPrincipal = 8,
            int maxSeedBytes = 262_144,
            int maxTracked = 64,
            int queueCapacity = 8)
        {
            PrincipalId = Guid.NewGuid();
            _keys =
            [
                new IntegrationApiKeySnapshot(Guid.NewGuid(),
                    PrincipalId,
                    KeyPrefix,
                    ReadOnlyMemory<byte>.Empty,
                    "primary",
                    AllowedTriggerIdsJson: null,
                    CreatedAtUtc: 1,
                    LastUsedAtUtc: null,
                    RevokedAtUtc: null)
            ];

            _keyStore = Substitute.For<IIntegrationApiKeyStore>();
            _ = _keyStore.GetByPrefixAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                         .Returns(call => _keys.SingleOrDefault(row => string.Equals(row.KeyPrefix, call.Arg<string>(), StringComparison.Ordinal)));

            Buffer = new IntegrationExecutionEventBuffer(Options.Create(new IntegrationOptions
                {
                    MaxTrackedExecutions = maxTracked
                }),
                TimeProvider.System);

            Queue = Channel.CreateBounded<Guid>(new BoundedChannelOptions(queueCapacity)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait
            });

            Persistence = Substitute.For<INodeChatPersistenceService>();

            Service = new IntegrationInvocationService(Triggers,
                _keyStore,
                Substitute.For<IIntegrationApiKeyService>(),
                Executions,
                Buffer,
                Persistence,
                Queue,
                Options.Create(new IntegrationOptions
                {
                    MaxQueuedExecutions = maxQueued,
                    MaxQueuedExecutionsPerPrincipal = maxQueuedPerPrincipal,
                    MaxSeedBytes = maxSeedBytes,
                    MaxTrackedExecutions = maxTracked
                }),
                TimeProvider.System,
                NullLogger<IntegrationInvocationService>.Instance);
        }

        public IntegrationExecutionEventBuffer Buffer { get; }

        public FakeIntegrationExecutionStore Executions { get; } = new();

        public INodeChatPersistenceService Persistence { get; }

        public Guid PrincipalId { get; }

        public Channel<Guid> Queue { get; }

        public IIntegrationInvocationService Service { get; }

        public FakeIntegrationTriggerStore Triggers { get; } = new();

        public IntegrationTriggerSnapshot SeedTrigger(string name,
            bool enabled = true,
            IntegrationSessionPolicy sessionPolicy = IntegrationSessionPolicy.PerInvocation,
            IntegrationInputKinds acceptedInputKinds = IntegrationInputKinds.Text | IntegrationInputKinds.Json) =>
            Triggers.Seed(name, Guid.NewGuid(), enabled, sessionPolicy, acceptedInputKinds);

        public void RestrictKeyTo(Guid triggerId) =>
            _keys[0] = _keys[0] with
            {
                AllowedTriggerIdsJson = $"[\"{triggerId}\"]"
            };

        public void RevokeKey() =>
            _keys[0] = _keys[0] with
            {
                RevokedAtUtc = 2
            };

        /// <summary>Issues a second credential for the SAME integrator, which is the rotation case ownership must survive.</summary>
        public void RotateCredential() =>
            _keys.Add(_keys[0] with
            {
                Id = Guid.NewGuid(),
                KeyPrefix = RotatedKeyPrefix,
                Label = "rotated"
            });

        public NodeChatCreateConversationRequest CapturedConversation() =>
            (NodeChatCreateConversationRequest)Persistence.ReceivedCalls()
                                                          .Single(static call => call.GetMethodInfo().Name == nameof(INodeChatPersistenceService.CreateConversationAsync))
                                                          .GetArguments()[0]!;

        public NodeChatPersistUserMessageRequest CapturedSeed() =>
            (NodeChatPersistUserMessageRequest)Persistence.ReceivedCalls()
                                                          .Single(static call => call.GetMethodInfo().Name == nameof(INodeChatPersistenceService.PersistUserMessageAsync))
                                                          .GetArguments()[0]!;

        public Task<IntegrationAcceptResult> AcceptAsync(string triggerName,
            IReadOnlyList<IntegrationInputDto>? inputs = null,
            Guid? requestId = null,
            byte[]? rawBody = null,
            Guid? sessionId = null,
            Guid? principalId = null,
            string keyPrefix = KeyPrefix)
        {
            return Service.AcceptAsync(Request(triggerName, inputs, requestId, rawBody, sessionId, principalId, keyPrefix));
        }

        private IntegrationAcceptRequest Request(string triggerName,
            IReadOnlyList<IntegrationInputDto>? inputs,
            Guid? requestId,
            byte[]? rawBody,
            Guid? sessionId,
            Guid? principalId,
            string keyPrefix)
        {
            if (principalId is { } stranger)
            {
                // A different integrator presenting its own credential, which is what makes the (principal, request id)
                // scoping observable at all.
                var strangerPrefix = $"xeint_{stranger:N}"[..14];
                if (!_keys.Any(row => string.Equals(row.KeyPrefix, strangerPrefix, StringComparison.Ordinal)))
                {
                    _keys.Add(_keys[0] with
                    {
                        Id = Guid.NewGuid(),
                        PrincipalId = stranger,
                        KeyPrefix = strangerPrefix,
                        Label = "stranger",
                        RevokedAtUtc = null
                    });
                }

                keyPrefix = strangerPrefix;
            }

            return new IntegrationAcceptRequest(triggerName,
                principalId ?? PrincipalId,
                keyPrefix,
                requestId ?? Guid.NewGuid(),
                sessionId,
                inputs ?? [Text("do the thing")],
                rawBody ?? Encoding.UTF8.GetBytes("""{"inputs":[]}"""));
        }
    }
}

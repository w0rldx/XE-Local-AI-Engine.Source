namespace XE_Local_AI_Engine.Tests.Integrations;

using System.Security.Claims;
using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Integrations;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="IntegrationExternalAccess" />: the one authorisation rule every external row-addressing route goes
///     through. The load-bearing property is that the five ways a caller can fail it are INDISTINGUISHABLE, and that a
///     key's trigger allowlist still binds after the invocation — a narrow key must not be able to read or cancel its
///     own principal's executions under a trigger it is explicitly excluded from.
/// </summary>
public sealed class IntegrationExternalAccessTests
{
    private const string BroadPrefix = "xeint_broad001";

    private const string NarrowPrefix = "xeint_narrow01";

    [Test]
    [Arguments(MaskingCause.UnknownExecution)]
    [Arguments(MaskingCause.ForeignPrincipal)]
    [Arguments(MaskingCause.TriggerOutsideAllowlist)]
    [Arguments(MaskingCause.RevokedKey)]
    [Arguments(MaskingCause.UnknownKeyPrefix)]
    public async Task ResolveExecution_MasksEveryCauseIdentically(MaskingCause cause)
    {
        var fixture = new Fixture();
        var (executionId, caller) = fixture.Arrange(cause);

        var result = await fixture.Access.ResolveExecutionAsync(executionId, caller);

        AssertEx.Equal(IntegrationAccessOutcome.Masked, result.Outcome, $"'{cause}' must be masked.");
        AssertEx.Null(result.Execution, "A masked result carries no row: the id is the capability, and confirming a row a caller may not see is the leak.");
    }

    [Test]
    public async Task ResolveExecution_WhenTheKeyAllowsEveryTrigger_Resolves()
    {
        var fixture = new Fixture();
        var executionId = fixture.SeedExecution(fixture.TriggerB, fixture.PrincipalId);

        var result = await fixture.Access.ResolveExecutionAsync(executionId, new IntegrationCallerIdentity(fixture.PrincipalId, BroadPrefix));

        AssertEx.Equal(IntegrationAccessOutcome.Allowed, result.Outcome, "A null allowlist means every trigger.");
        AssertEx.Equal(executionId, AssertEx.NotNull(result.Execution).Id);
    }

    [Test]
    public async Task ResolveExecution_WhenTwoKeysShareAPrincipal_TheNarrowOneStillCannotSeeTheOtherTrigger()
    {
        // The exact scenario the round-5 review described: issuing a narrow ingest key alongside a broad admin key for
        // the same integrator is the use case principals were added for, and principal-only masking let the narrow one
        // read and cancel everything.
        var fixture = new Fixture();
        var executionId = fixture.SeedExecution(fixture.TriggerB, fixture.PrincipalId);

        var broad = await fixture.Access.ResolveExecutionAsync(executionId, new IntegrationCallerIdentity(fixture.PrincipalId, BroadPrefix));
        var narrow = await fixture.Access.ResolveExecutionAsync(executionId, new IntegrationCallerIdentity(fixture.PrincipalId, NarrowPrefix));

        AssertEx.Equal(IntegrationAccessOutcome.Allowed, broad.Outcome, "The broad key of the owning principal reads its own execution.");
        AssertEx.Equal(IntegrationAccessOutcome.Masked, narrow.Outcome, "The narrow key is scoped to another trigger, so this row must not be confirmable.");
    }

    [Test]
    public async Task ResolveExecution_RereadsTheKeyRowOnEveryCall()
    {
        var fixture = new Fixture();
        var executionId = fixture.SeedExecution(fixture.TriggerB, fixture.PrincipalId);
        var caller = new IntegrationCallerIdentity(fixture.PrincipalId, BroadPrefix);

        var before = await fixture.Access.ResolveExecutionAsync(executionId, caller);
        fixture.NarrowBroadKeyToTriggerA();
        var after = await fixture.Access.ResolveExecutionAsync(executionId, caller);

        AssertEx.Equal(IntegrationAccessOutcome.Allowed, before.Outcome);
        AssertEx.Equal(IntegrationAccessOutcome.Masked, after.Outcome,
            "The allowlist is not a claim, so narrowing a key has to take effect on the next request rather than at the next key mint.");
    }

    [Test]
    public async Task ResolveExecution_DoesTheSameStoreWorkWhetherTheRowExistsOrNot()
    {
        // The masked bodies were already byte-identical, but the WORK was not: a row that exists and is merely outside
        // the key's allowlist performed the key query and the allowlist scan, while an unknown id skipped both. That
        // difference is measurable from a same-host process holding a narrow key, and it answers "does this execution
        // id exist" behind two identical 404s.
        var fixture = new Fixture();
        var caller = new IntegrationCallerIdentity(fixture.PrincipalId, NarrowPrefix);

        var existing = fixture.SeedExecution(fixture.TriggerB, fixture.PrincipalId);
        var before = fixture.KeyReads;
        AssertEx.Equal(IntegrationAccessOutcome.Masked, (await fixture.Access.ResolveExecutionAsync(existing, caller)).Outcome);
        var forExisting = fixture.KeyReads - before;

        before = fixture.KeyReads;
        AssertEx.Equal(IntegrationAccessOutcome.Masked, (await fixture.Access.ResolveExecutionAsync(Guid.NewGuid(), caller)).Outcome);
        var forUnknown = fixture.KeyReads - before;

        AssertEx.Equal(forExisting, forUnknown, "An existing-but-disallowed execution must cost exactly what an unknown one costs.");
        AssertEx.Equal(expected: 1, forExisting, "Both paths perform the key read; neither short-circuits past it.");
    }

    [Test]
    public async Task ResolveSession_DoesTheSameStoreWorkWhetherTheRowExistsOrNot()
    {
        var fixture = new Fixture();
        var caller = new IntegrationCallerIdentity(fixture.PrincipalId, NarrowPrefix);

        var existing = fixture.SeedSession(fixture.TriggerB, fixture.PrincipalId);
        var before = fixture.KeyReads;
        AssertEx.Equal(IntegrationAccessOutcome.Masked, (await fixture.Access.ResolveSessionAsync(existing, caller)).Outcome);
        var forExisting = fixture.KeyReads - before;

        before = fixture.KeyReads;
        AssertEx.Equal(IntegrationAccessOutcome.Masked, (await fixture.Access.ResolveSessionAsync(Guid.NewGuid(), caller)).Outcome);
        var forUnknown = fixture.KeyReads - before;

        AssertEx.Equal(forExisting, forUnknown, "An existing-but-disallowed session must cost exactly what an unknown one costs.");
        AssertEx.Equal(expected: 1, forExisting);
    }

    [Test]
    public async Task ResolveSession_AppliesTheSameRuleAgainstTheSessionsTrigger()
    {
        var fixture = new Fixture();
        var sessionId = fixture.SeedSession(fixture.TriggerB, fixture.PrincipalId);

        var broad = await fixture.Access.ResolveSessionAsync(sessionId, new IntegrationCallerIdentity(fixture.PrincipalId, BroadPrefix));
        var narrow = await fixture.Access.ResolveSessionAsync(sessionId, new IntegrationCallerIdentity(fixture.PrincipalId, NarrowPrefix));
        var foreign = await fixture.Access.ResolveSessionAsync(sessionId, new IntegrationCallerIdentity(Guid.NewGuid(), BroadPrefix));
        var unknown = await fixture.Access.ResolveSessionAsync(Guid.NewGuid(), new IntegrationCallerIdentity(fixture.PrincipalId, BroadPrefix));

        AssertEx.Equal(IntegrationAccessOutcome.Allowed, broad.Outcome);
        AssertEx.Equal(IntegrationAccessOutcome.Masked, narrow.Outcome, "The session rule is the execution rule, against session.TriggerId.");
        AssertEx.Equal(IntegrationAccessOutcome.Masked, foreign.Outcome);
        AssertEx.Equal(IntegrationAccessOutcome.Masked, unknown.Outcome,
            "The store's two-column read answers a missing row and a foreign one with the same non-result.");
        AssertEx.Null(foreign.Session, "A masked result never carries the row.");
    }

    [Test]
    public void FromPrincipal_FailsClosedOnAMissingOrDuplicatedClaim()
    {
        AssertEx.Null(IntegrationCallerIdentity.FromPrincipal(principal: null), "An unauthenticated principal carries no identity.");
        AssertEx.Null(IntegrationCallerIdentity.FromPrincipal(Principal([])), "A principal with neither claim must fail closed.");
        AssertEx.Null(IntegrationCallerIdentity.FromPrincipal(Principal([
                new Claim(NodeAuthorizationPolicies.IntegrationPrincipalClaimType, Guid.NewGuid().ToString("D")),
                new Claim(NodeAuthorizationPolicies.IntegrationPrincipalClaimType, Guid.NewGuid().ToString("D")),
                new Claim(NodeAuthorizationPolicies.IntegrationKeyPrefixClaimType, BroadPrefix)
            ])),
            "Two principal claims is not a shape this node's handler can produce, so taking the first would be trusting something else's identity.");

        var principalId = Guid.NewGuid();
        var resolved = IntegrationCallerIdentity.FromPrincipal(Principal([
            new Claim(NodeAuthorizationPolicies.IntegrationPrincipalClaimType, principalId.ToString("D")),
            new Claim(NodeAuthorizationPolicies.IntegrationKeyPrefixClaimType, BroadPrefix)
        ]));
        var identity = AssertEx.NotNull(resolved);
        AssertEx.Equal(principalId, identity.PrincipalId);
        AssertEx.Equal(BroadPrefix, identity.KeyPrefix);
    }

    private static ClaimsPrincipal Principal(Claim[] claims) =>
        new(new ClaimsIdentity(claims, "IntegrationApiKey"));

    /// <summary>The five ways a caller fails the rule. They must all answer the same masked 404.</summary>
    public enum MaskingCause
    {
        UnknownExecution,
        ForeignPrincipal,
        TriggerOutsideAllowlist,
        RevokedKey,
        UnknownKeyPrefix
    }

    private sealed class Fixture
    {
        private readonly FakeIntegrationApiKeyStore _keys = new();
        private readonly FakeIntegrationExecutionStore _executions = new();
        private readonly FakeIntegrationSessionStore _sessions = new();

        public Fixture()
        {
            PrincipalId = Guid.NewGuid();
            TriggerA = Guid.NewGuid();
            TriggerB = Guid.NewGuid();

            _ = _keys.CreateAsync(new IntegrationApiKeyCreateCommand(Guid.NewGuid(),
                PrincipalId,
                BroadPrefix,
                new byte[]
                {
                    1
                },
                "broad",
                AllowedTriggerIdsJson: null)).GetAwaiter().GetResult();
            _ = _keys.CreateAsync(new IntegrationApiKeyCreateCommand(Guid.NewGuid(),
                PrincipalId,
                NarrowPrefix,
                new byte[]
                {
                    2
                },
                "narrow",
                JsonSerializer.Serialize(new[]
                {
                    TriggerA
                }))).GetAwaiter().GetResult();

            Access = new IntegrationExternalAccess(_executions, _sessions, _keys);
        }

        public IntegrationExternalAccess Access { get; }

        public Guid PrincipalId { get; }

        public Guid TriggerA { get; }

        public Guid TriggerB { get; }

        public (Guid ExecutionId, IntegrationCallerIdentity Caller) Arrange(MaskingCause cause)
        {
            switch (cause)
            {
                case MaskingCause.UnknownExecution:
                    return (Guid.NewGuid(), new IntegrationCallerIdentity(PrincipalId, BroadPrefix));
                case MaskingCause.ForeignPrincipal:
                    return (SeedExecution(TriggerA, Guid.NewGuid()), new IntegrationCallerIdentity(PrincipalId, BroadPrefix));
                case MaskingCause.TriggerOutsideAllowlist:
                    return (SeedExecution(TriggerB, PrincipalId), new IntegrationCallerIdentity(PrincipalId, NarrowPrefix));
                case MaskingCause.RevokedKey:
                    var revoked = SeedExecution(TriggerA, PrincipalId);
                    var row = _keys.Rows.Single(candidate => string.Equals(candidate.KeyPrefix, BroadPrefix, StringComparison.Ordinal));
                    _ = _keys.RevokeAsync(row.Id, atUtc: 1).GetAwaiter().GetResult();
                    return (revoked, new IntegrationCallerIdentity(PrincipalId, BroadPrefix));
                default:
                    return (SeedExecution(TriggerA, PrincipalId), new IntegrationCallerIdentity(PrincipalId, "xeint_nosuch01"));
            }
        }

        public Guid SeedExecution(Guid triggerId, Guid principalId)
        {
            var executionId = Guid.NewGuid();
            var sessionId = Guid.NewGuid();
            _ = _executions.AcceptAsync(new IntegrationAcceptCommand(new IntegrationSessionCreate(sessionId, triggerId, Guid.NewGuid(), Guid.NewGuid()),
                    executionId,
                    triggerId,
                    sessionId,
                    principalId,
                    Guid.NewGuid(),
                    ReadOnlyMemory<byte>.Empty,
                    BroadPrefix,
                    ReceivedAtUtc: 1,
                    new IntegrationEventAppend(Guid.NewGuid(), executionId, Sequence: 1, IntegrationStreamEventTypes.ExecutionAccepted, DetailJson: null, OccurredAtUtc: 1)),
                maxActive: 1024,
                maxActivePerPrincipal: 1024).GetAwaiter().GetResult();
            return executionId;
        }

        public Guid SeedSession(Guid triggerId, Guid principalId)
        {
            var sessionId = Guid.NewGuid();
            var seeded = _sessions.Seed(sessionId, triggerId, Guid.NewGuid(), Guid.NewGuid());
            _sessions.Reassign(seeded.Id, principalId);
            return sessionId;
        }

        /// <summary>Key reads served so far, which is the observable half of the constant-shape rule.</summary>
        public int KeyReads => _keys.GetByPrefixCalls;

        public void NarrowBroadKeyToTriggerA()
        {
            var row = _keys.Rows.Single(candidate => string.Equals(candidate.KeyPrefix, BroadPrefix, StringComparison.Ordinal));
            _keys.Rescope(row.Id, JsonSerializer.Serialize(new[]
            {
                TriggerA
            }));
        }
    }
}

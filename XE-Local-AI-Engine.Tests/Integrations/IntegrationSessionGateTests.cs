namespace XE_Local_AI_Engine.Tests.Integrations;

using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Integrations;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The caller-managed session gate, driven through the REAL accept path rather than through the gate method alone,
///     because what the gate decides only matters together with what the accept path then writes.
///     <para>
///         Two properties carry it: every case a caller could use to probe for a session it may not see answers ONE
///         404 with a byte-identical body, and the per-session critical section covers the busy read AND the seed write
///         it authorises, so two accepts can never land two seeds in one conversation.
///     </para>
/// </summary>
public sealed class IntegrationSessionGateTests
{
    [Test]
    public async Task Resolve_ForAnUnknownSession_Returns404()
    {
        var harness = new IntegrationInvokeHarness();
        var trigger = harness.SeedTrigger("caller-managed", sessionPolicy: IntegrationSessionPolicy.CallerManaged);

        var result = await harness.AcceptAsync(trigger.Name, sessionId: Guid.NewGuid()).ConfigureAwait(false);

        AssertEx.Equal(IntegrationAcceptOutcome.SessionNotFound, result.Outcome);
        AssertEx.Empty(harness.Executions.Rows);
    }

    [Test]
    public async Task Resolve_ForAnotherTriggersSession_Returns404NotForbidden()
    {
        var harness = new IntegrationInvokeHarness();
        var mine = harness.SeedTrigger("mine", sessionPolicy: IntegrationSessionPolicy.CallerManaged);
        var other = harness.SeedTrigger("other", sessionPolicy: IntegrationSessionPolicy.CallerManaged);
        var session = harness.SeedSession(other.Id);

        var unknown = await harness.AcceptAsync(mine.Name, sessionId: Guid.NewGuid()).ConfigureAwait(false);
        var foreignTrigger = await harness.AcceptAsync(mine.Name, sessionId: session.Id).ConfigureAwait(false);

        AssertEx.Equal(IntegrationAcceptOutcome.SessionNotFound, foreignTrigger.Outcome);
        AssertEx.Equal(unknown.Message, foreignTrigger.Message, "A distinguishable body re-opens the oracle the status code just closed.");
        AssertEx.Empty(harness.Executions.Rows);
    }

    [Test]
    public async Task Resolve_ForAnotherPrincipal_Returns404NotForbidden()
    {
        var harness = new IntegrationInvokeHarness();
        var trigger = harness.SeedTrigger("caller-managed", sessionPolicy: IntegrationSessionPolicy.CallerManaged);
        var session = harness.SeedSession(trigger.Id, principalId: Guid.NewGuid());

        var unknown = await harness.AcceptAsync(trigger.Name, sessionId: Guid.NewGuid()).ConfigureAwait(false);
        var foreign = await harness.AcceptAsync(trigger.Name, sessionId: session.Id).ConfigureAwait(false);

        AssertEx.Equal(IntegrationAcceptOutcome.SessionNotFound, foreign.Outcome);
        AssertEx.Equal(unknown.Message, foreign.Message);
        AssertEx.Empty(harness.Executions.Rows);
    }

    [Test]
    public async Task Resolve_ForASecondKeyOfTheSamePrincipal_Continues()
    {
        // A key INSTANCE is not an integrator. Rotating a credential, or splitting ingest and read keys, must not
        // strand the sessions the replaced key owned — which is why ownership keys on PrincipalId.
        var harness = new IntegrationInvokeHarness();
        var trigger = harness.SeedTrigger("caller-managed", sessionPolicy: IntegrationSessionPolicy.CallerManaged);
        var session = harness.SeedSession(trigger.Id);
        harness.RotateCredential();

        var result = await harness.AcceptAsync(trigger.Name, sessionId: session.Id, keyPrefix: IntegrationInvokeHarness.RotatedKeyPrefix).ConfigureAwait(false);

        AssertEx.Equal(IntegrationAcceptOutcome.Accepted, result.Outcome);
        AssertEx.Equal(session.Id, result.SessionId);
    }

    [Test]
    public async Task Resolve_WhenTheCurrentKeysAllowlistExcludesTheTrigger_Returns404()
    {
        // The limb principal ownership alone does not cover: a second key of the SAME integrator, deliberately scoped
        // to one trigger, must not reach that integrator's sessions under every other trigger.
        var harness = new IntegrationInvokeHarness();
        var trigger = harness.SeedTrigger("caller-managed", sessionPolicy: IntegrationSessionPolicy.CallerManaged);
        var sibling = harness.SeedTrigger("sibling", sessionPolicy: IntegrationSessionPolicy.CallerManaged);
        var session = harness.SeedSession(trigger.Id);

        var allowed = await harness.SessionService.GetForExternalCallerAsync(session.Id, harness.Caller()).ConfigureAwait(false);
        harness.RestrictKeyTo(sibling.Id);
        var narrowed = await harness.SessionService.GetForExternalCallerAsync(session.Id, harness.Caller()).ConfigureAwait(false);
        var continued = await harness.AcceptAsync(trigger.Name, sessionId: session.Id).ConfigureAwait(false);

        _ = AssertEx.NotNull(allowed, "A broad key reaches its own principal's session.");
        AssertEx.Null(narrowed, "A key narrowed AFTER the session was created is refused on its next call — the key row is re-read per request.");

        // On the CONTINUE path the same narrowing is caught one step earlier, by the trigger allowlist, and answers the
        // byte-identical 404. Which of the two outcomes it is does not reach the caller — both map to one masked 404 —
        // so what this asserts is that the narrowed key reaches neither the trigger nor the session, and writes nothing.
        AssertEx.Contains(new[] { IntegrationAcceptOutcome.TriggerNotFound, IntegrationAcceptOutcome.SessionNotFound }, continued.Outcome);
        AssertEx.Empty(harness.Executions.Rows);
    }

    [Test]
    public async Task Resolve_AndTheExternalGet_UseTheSameHelper()
    {
        // The anti-drift assertion: one session/key pair, the same verdict through the continue gate and through the
        // external read. Two locally composed rules can disagree; one helper cannot.
        var harness = new IntegrationInvokeHarness();
        var trigger = harness.SeedTrigger("caller-managed", sessionPolicy: IntegrationSessionPolicy.CallerManaged);
        var foreign = harness.SeedSession(trigger.Id, principalId: Guid.NewGuid());

        var read = await harness.SessionService.GetForExternalCallerAsync(foreign.Id, harness.Caller()).ConfigureAwait(false);
        var continued = await harness.AcceptAsync(trigger.Name, sessionId: foreign.Id).ConfigureAwait(false);

        AssertEx.Null(read);
        AssertEx.Equal(IntegrationAcceptOutcome.SessionNotFound, continued.Outcome);
    }

    [Test]
    public async Task Resolve_WhenTheAgentOffersANonReadLocalTool_Returns422SessionPolicy()
    {
        // Ruling R4-9(a), re-checked at ACCEPT and not only at save: an agent's tools can change afterwards, and a
        // caller-managed session persists no tool history, so a continued run could not tell an action it already
        // performed from prose describing one.
        var harness = new IntegrationInvokeHarness
        {
            AgentIsReadLocalOnly = false
        };
        var trigger = harness.SeedTrigger("caller-managed", sessionPolicy: IntegrationSessionPolicy.CallerManaged);
        var session = harness.SeedSession(trigger.Id);

        var fresh = await harness.AcceptAsync(trigger.Name).ConfigureAwait(false);
        var continued = await harness.AcceptAsync(trigger.Name, sessionId: session.Id).ConfigureAwait(false);

        AssertEx.Equal(IntegrationAcceptOutcome.SessionPolicyRejected, fresh.Outcome);
        AssertEx.Equal(IntegrationAcceptOutcome.SessionPolicyRejected, continued.Outcome);
        AssertEx.Empty(harness.Executions.Rows);
        AssertEx.Empty(harness.CapturedConversations());
        AssertEx.Empty(harness.CapturedSeeds());
    }

    [Test]
    public async Task Resolve_ForAPerInvocationTrigger_SkipsTheToolCategoryCheckEntirely()
    {
        // A per-invocation trigger starts fresh every time, so it carries no history a missing tool call could make
        // wrong. Running the predicate for it would be a read that decides nothing.
        var harness = new IntegrationInvokeHarness
        {
            AgentIsReadLocalOnly = false
        };
        var trigger = harness.SeedTrigger("per-invocation");

        var result = await harness.AcceptAsync(trigger.Name).ConfigureAwait(false);

        AssertEx.Equal(IntegrationAcceptOutcome.Accepted, result.Outcome);
        _ = harness.TriggerService.DidNotReceive().AllowsCallerManagedAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Resolve_WhenClosed_Returns409SessionClosed()
    {
        var harness = new IntegrationInvokeHarness();
        var trigger = harness.SeedTrigger("caller-managed", sessionPolicy: IntegrationSessionPolicy.CallerManaged);
        var session = harness.SeedSession(trigger.Id, status: IntegrationSessionStatus.Closed);

        var result = await harness.AcceptAsync(trigger.Name, sessionId: session.Id).ConfigureAwait(false);

        AssertEx.Equal(IntegrationAcceptOutcome.SessionClosed, result.Outcome);
        AssertEx.Empty(harness.Executions.Rows);
    }

    [Test]
    [Arguments(IntegrationExecutionStatus.Accepted)]
    [Arguments(IntegrationExecutionStatus.Queued)]
    [Arguments(IntegrationExecutionStatus.Running)]
    public async Task Resolve_WhenAnExecutionIsActive_Returns409SessionBusy(IntegrationExecutionStatus status)
    {
        var harness = new IntegrationInvokeHarness();
        var trigger = harness.SeedTrigger("caller-managed", sessionPolicy: IntegrationSessionPolicy.CallerManaged);
        var session = harness.SeedSession(trigger.Id);
        _ = harness.Executions.Seed(Guid.NewGuid(), trigger.Id, session.Id, status);

        var result = await harness.AcceptAsync(trigger.Name, sessionId: session.Id).ConfigureAwait(false);

        AssertEx.Equal(IntegrationAcceptOutcome.SessionBusy, result.Outcome);
        AssertEx.Equal(expected: 1, harness.Executions.Rows.Count, "A refused continuation writes nothing.");
    }

    [Test]
    public async Task Resolve_WhenThePreviousExecutionCompleted_Continues()
    {
        var harness = new IntegrationInvokeHarness();
        var trigger = harness.SeedTrigger("caller-managed", sessionPolicy: IntegrationSessionPolicy.CallerManaged);
        var session = harness.SeedSession(trigger.Id);
        _ = harness.Executions.Seed(Guid.NewGuid(), trigger.Id, session.Id, IntegrationExecutionStatus.Completed);

        var result = await harness.AcceptAsync(trigger.Name, sessionId: session.Id).ConfigureAwait(false);

        AssertEx.Equal(IntegrationAcceptOutcome.Accepted, result.Outcome);
        AssertEx.Equal(session.Id, result.SessionId);

        // The EXISTING conversation, never a new one: the continuation is the whole point of a caller-managed session.
        AssertEx.Empty(harness.CapturedConversations());
        AssertEx.Equal(session.ConversationId, harness.CapturedSeed().ConversationId);
    }

    [Test]
    public async Task TwoSequentialAccepts_TheSecondReturns409SessionBusy()
    {
        var harness = new IntegrationInvokeHarness();
        var trigger = harness.SeedTrigger("caller-managed", sessionPolicy: IntegrationSessionPolicy.CallerManaged);
        var session = harness.SeedSession(trigger.Id);

        var first = await harness.AcceptAsync(trigger.Name, sessionId: session.Id).ConfigureAwait(false);
        var second = await harness.AcceptAsync(trigger.Name, sessionId: session.Id).ConfigureAwait(false);

        AssertEx.Equal(IntegrationAcceptOutcome.Accepted, first.Outcome);
        AssertEx.Equal(IntegrationAcceptOutcome.SessionBusy, second.Outcome);
        AssertEx.Equal(expected: 1, harness.Executions.Rows.Count);
    }

    [Test]
    public async Task TwoConcurrentAcceptsOnOneSession_OnlyOneIsAccepted()
    {
        // The cross-contamination case, not a throughput test: two seeds in ONE conversation would make the first
        // execution read the second caller's input as history.
        var harness = new IntegrationInvokeHarness();
        var trigger = harness.SeedTrigger("caller-managed", sessionPolicy: IntegrationSessionPolicy.CallerManaged);
        var session = harness.SeedSession(trigger.Id);

        var results = await Task.WhenAll(Task.Run(() => harness.AcceptAsync(trigger.Name, sessionId: session.Id)),
                                    Task.Run(() => harness.AcceptAsync(trigger.Name, sessionId: session.Id)))
                                .ConfigureAwait(false);

        AssertEx.Equal(expected: 1, results.Count(static result => result.Outcome == IntegrationAcceptOutcome.Accepted));
        AssertEx.Equal(expected: 1, results.Count(static result => result.Outcome == IntegrationAcceptOutcome.SessionBusy));
        AssertEx.Equal(expected: 1, harness.CapturedSeeds().Count, "The conversation must hold exactly ONE new seed.");
    }

    [Test]
    public async Task ConcurrentAcceptsOnTwoSessions_BothProceed()
    {
        // The gate is per SESSION. Without this a regression to one static semaphore passes every other test here.
        var harness = new IntegrationInvokeHarness();
        var trigger = harness.SeedTrigger("caller-managed", sessionPolicy: IntegrationSessionPolicy.CallerManaged);
        var first = harness.SeedSession(trigger.Id);
        var second = harness.SeedSession(trigger.Id);

        var results = await Task.WhenAll(Task.Run(() => harness.AcceptAsync(trigger.Name, sessionId: first.Id)),
                                    Task.Run(() => harness.AcceptAsync(trigger.Name, sessionId: second.Id)))
                                .ConfigureAwait(false);

        AssertEx.True(results.All(static result => result.Outcome == IntegrationAcceptOutcome.Accepted),
            "Two different caller-managed sessions do not contend.");
    }

    [Test]
    public async Task DeleteRacingAnAccept_DoesNotPurgeAnAcceptedRun()
    {
        // A delete that read "not busy" while an accept sat between its own read and the admission transaction would
        // purge the conversation out from under a run the caller already holds a 202 for.
        var harness = new IntegrationInvokeHarness();
        var trigger = harness.SeedTrigger("caller-managed", sessionPolicy: IntegrationSessionPolicy.CallerManaged);
        var session = harness.SeedSession(trigger.Id);

        using var lease = await harness.SessionGate.EnterAsync(session.Id).ConfigureAwait(false);
        var delete = Task.Run(() => harness.SessionService.DeleteAsync(session.Id));

        // The delete is parked on the gate this test holds, exactly where the accept path would hold it.
        AssertEx.False(delete.IsCompleted, "A delete must wait for the gate rather than read past it.");
        _ = harness.Executions.Seed(Guid.NewGuid(), trigger.Id, session.Id, IntegrationExecutionStatus.Accepted);
        lease.Dispose();

        AssertEx.Equal(IntegrationSessionDeleteOutcome.Busy, await delete.ConfigureAwait(false));
        AssertEx.Equal(expected: 1, harness.Sessions.Rows.Count, "The session survives a refused delete.");
        AssertEx.Equal(expected: 1, harness.Executions.Rows.Count);
    }
}

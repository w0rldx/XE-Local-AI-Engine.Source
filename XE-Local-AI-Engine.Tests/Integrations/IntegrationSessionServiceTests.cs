namespace XE_Local_AI_Engine.Tests.Integrations;

using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Integrations;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The session lifecycle around the gate: what an accept writes for a NEW caller-managed session versus a
///     continuation, what an operator's page and delete do, and what the integrator's own read may see.
/// </summary>
public sealed class IntegrationSessionServiceTests
{
    [Test]
    public async Task NewCallerManagedSession_OwnsAConversationWithIntegrationKind()
    {
        var harness = new IntegrationInvokeHarness();
        var trigger = harness.SeedTrigger("caller-managed", sessionPolicy: IntegrationSessionPolicy.CallerManaged);

        var result = await harness.AcceptAsync(trigger.Name).ConfigureAwait(false);

        AssertEx.Equal(IntegrationAcceptOutcome.Accepted, result.Outcome);

        var conversation = harness.CapturedConversation();
        AssertEx.Equal(NodeConversationKind.Integration, conversation.Kind);

        // R4-1: the conversation is created at the id the admission transaction already recorded, AFTER that commit —
        // which is what makes an orphan conversation impossible rather than merely unlikely.
        AssertEx.Equal(expected: 1, harness.Executions.CreatedSessions.Count);
        AssertEx.True(conversation.ConversationId.HasValue, "The accept path supplies the pre-minted conversation id.");
        var conversationId = conversation.ConversationId.GetValueOrDefault();
        AssertEx.Equal(harness.Executions.CreatedSessions[0].ConversationId, conversationId);
        AssertEx.Equal(conversationId, harness.CapturedSeed().ConversationId);
    }

    [Test]
    public async Task ContinueWritesNoSecondConversation()
    {
        var harness = new IntegrationInvokeHarness();
        var trigger = harness.SeedTrigger("caller-managed", sessionPolicy: IntegrationSessionPolicy.CallerManaged);
        var session = harness.SeedSession(trigger.Id);

        var result = await harness.AcceptAsync(trigger.Name, sessionId: session.Id).ConfigureAwait(false);

        AssertEx.Equal(IntegrationAcceptOutcome.Accepted, result.Outcome);
        AssertEx.Empty(harness.CapturedConversations());
        AssertEx.Empty(harness.Executions.CreatedSessions);
        AssertEx.Equal(session.ConversationId, harness.CapturedSeed().ConversationId);
    }

    [Test]
    public async Task Accept_BumpsExecutionCountAndLastActivity()
    {
        var harness = new IntegrationInvokeHarness();
        var trigger = harness.SeedTrigger("caller-managed", sessionPolicy: IntegrationSessionPolicy.CallerManaged);
        var session = harness.SeedSession(trigger.Id, executionCount: 0);

        var first = await harness.AcceptAsync(trigger.Name, sessionId: session.Id).ConfigureAwait(false);
        harness.Executions.Complete(first.ExecutionId!.Value);
        _ = await harness.AcceptAsync(trigger.Name, sessionId: session.Id).ConfigureAwait(false);

        var row = harness.Sessions.Rows.Single(candidate => candidate.Id == session.Id);
        AssertEx.Equal(expected: 2, row.ExecutionCount);
        AssertEx.True(row.LastActivityUtc > 0, "The accept transaction moves the session's activity stamp.");
    }

    [Test]
    public async Task Accept_WhenTheSessionClosesInsideTheAdmissionWindow_AnswersTheMasked404()
    {
        // The race-free backstop: the store's session bump is scoped to the caller's own ACTIVE row and abandons the
        // transaction when it matches none. The gate answers the precise code on every path a caller can reach; this is
        // what covers the window between that check and the commit, and it answers the SAME masked 404.
        var harness = new IntegrationInvokeHarness();
        var trigger = harness.SeedTrigger("caller-managed", sessionPolicy: IntegrationSessionPolicy.CallerManaged);
        var session = harness.SeedSession(trigger.Id);
        harness.Sessions.Forget(session.Id);

        var result = await harness.AcceptAsync(trigger.Name, sessionId: session.Id).ConfigureAwait(false);

        AssertEx.Equal(IntegrationAcceptOutcome.SessionNotFound, result.Outcome);
    }

    [Test]
    public async Task Delete_PurgesTheConversationAndItsExecutions()
    {
        // Deleting a session IS purging its conversation: the footprint purge takes the session row, its executions and
        // their events with it, because those rows carry conversation-derived content. Only the content-free audit rows
        // survive, and they do so because their ConversationId is null.
        var harness = new IntegrationInvokeHarness();
        var trigger = harness.SeedTrigger("caller-managed", sessionPolicy: IntegrationSessionPolicy.CallerManaged);
        var session = harness.SeedSession(trigger.Id);

        var outcome = await harness.SessionService.DeleteAsync(session.Id).ConfigureAwait(false);

        AssertEx.Equal(IntegrationSessionDeleteOutcome.Deleted, outcome);

        var purge = (NodeChatDeleteConversationRequest)harness.Persistence.ReceivedCalls()
                                                              .Single(static call => call.GetMethodInfo().Name == nameof(INodeChatPersistenceService.DeleteConversationAsync))
                                                              .GetArguments()[0]!;
        AssertEx.Equal(session.ConversationId, purge.ConversationId);
        AssertEx.True(purge.PurgeImmediately, "A soft delete would leave the session's executions readable.");
        AssertEx.Empty(harness.Sessions.Rows);
    }

    [Test]
    public async Task Delete_ForAnUnknownSession_IsNotFound()
    {
        var harness = new IntegrationInvokeHarness();

        AssertEx.Equal(IntegrationSessionDeleteOutcome.NotFound, await harness.SessionService.DeleteAsync(Guid.NewGuid()).ConfigureAwait(false));
    }

    [Test]
    [Arguments(IntegrationExecutionStatus.Accepted)]
    [Arguments(IntegrationExecutionStatus.Queued)]
    [Arguments(IntegrationExecutionStatus.Running)]
    public async Task Delete_WhileAnExecutionIsRunning_Returns409(IntegrationExecutionStatus status)
    {
        var harness = new IntegrationInvokeHarness();
        var trigger = harness.SeedTrigger("caller-managed", sessionPolicy: IntegrationSessionPolicy.CallerManaged);
        var session = harness.SeedSession(trigger.Id);
        _ = harness.Executions.Seed(Guid.NewGuid(), trigger.Id, session.Id, status);

        AssertEx.Equal(IntegrationSessionDeleteOutcome.Busy, await harness.SessionService.DeleteAsync(session.Id).ConfigureAwait(false));
        AssertEx.Equal(expected: 1, harness.Sessions.Rows.Count);
        _ = harness.Persistence.DidNotReceive().DeleteConversationAsync(Arg.Any<NodeChatDeleteConversationRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task InvokesNamingSessionsThatDoNotExist_LeaveNoGateEntriesBehind()
    {
        // The gate is entered for ANY non-null session id, before anything has decided the session exists. Without the
        // forget an authenticated integrator looping invoke with random GUIDs adds one SemaphoreSlim per call,
        // permanently: the per-principal limiter bounds the rate, not the total.
        var harness = new IntegrationInvokeHarness();
        var callerManaged = harness.SeedTrigger("caller-managed", sessionPolicy: IntegrationSessionPolicy.CallerManaged);
        var perInvocation = harness.SeedTrigger("per-invocation");

        for (var i = 0; i < 8; i++)
        {
            var result = await harness.AcceptAsync(callerManaged.Name, sessionId: Guid.NewGuid()).ConfigureAwait(false);
            AssertEx.Equal(IntegrationAcceptOutcome.SessionNotFound, result.Outcome);
        }

        // The policy branch answers before the ownership read and mints an entry of its own.
        var named = await harness.AcceptAsync(perInvocation.Name, sessionId: Guid.NewGuid()).ConfigureAwait(false);
        AssertEx.Equal(IntegrationAcceptOutcome.SessionNotFound, named.Outcome);

        AssertEx.Equal(expected: 0, harness.SessionGate.TrackedCount, "An id with no row behind it must leave no gate entry.");
    }

    [Test]
    public async Task AnInvokeNamingAnotherPrincipalsSession_KeepsThatSessionsGateEntry()
    {
        // The other half of the rule: the answer is the same masked 404, but the row EXISTS, and dropping its entry
        // would let its owner's next accept mint a second semaphore and enter while a first accept is still inside.
        var harness = new IntegrationInvokeHarness();
        var trigger = harness.SeedTrigger("caller-managed", sessionPolicy: IntegrationSessionPolicy.CallerManaged);
        var foreign = harness.SeedSession(trigger.Id, principalId: Guid.NewGuid());

        var result = await harness.AcceptAsync(trigger.Name, sessionId: foreign.Id).ConfigureAwait(false);

        AssertEx.Equal(IntegrationAcceptOutcome.SessionNotFound, result.Outcome);
        AssertEx.Equal(expected: 1, harness.SessionGate.TrackedCount, "A session that exists keeps the mutual exclusion its owner depends on.");
    }

    [Test]
    public async Task DeletingASessionThatDoesNotExist_LeavesNoGateEntryBehind()
    {
        var harness = new IntegrationInvokeHarness();

        AssertEx.Equal(IntegrationSessionDeleteOutcome.NotFound, await harness.SessionService.DeleteAsync(Guid.NewGuid()).ConfigureAwait(false));
        AssertEx.Equal(expected: 0, harness.SessionGate.TrackedCount, "The delete path mints an entry the same way the invoke path does.");
    }

    [Test]
    public async Task Close_IsIdempotentAndForgetsTheGateEntry()
    {
        // Its callers close a session whose executions are already terminal — the coordinator after a PerInvocation
        // run, and the startup sweep for rows it has just failed. A busy refusal there would leave such a session
        // Active forever, which is why this half deliberately has none.
        var harness = new IntegrationInvokeHarness();
        var trigger = harness.SeedTrigger("per-invocation");
        var session = harness.SeedSession(trigger.Id);

        AssertEx.True(await harness.SessionService.CloseAsync(session.Id).ConfigureAwait(false));
        AssertEx.True(await harness.SessionService.CloseAsync(session.Id).ConfigureAwait(false));
        AssertEx.Equal(IntegrationSessionStatus.Closed, harness.Sessions.Rows.Single().Status);
        AssertEx.Equal(expected: 0, harness.SessionGate.TrackedCount, "A closed session leaves no gate entry behind.");
    }

    [Test]
    public async Task List_OrdersByLastActivityThenId_AndPagesDeterministically()
    {
        // Duplicate activity stamps are the case an unordered Skip/Take drops or duplicates rows across pages.
        var harness = new IntegrationInvokeHarness();
        var trigger = harness.SeedTrigger("caller-managed", sessionPolicy: IntegrationSessionPolicy.CallerManaged);
        for (var i = 0; i < 6; i++)
        {
            _ = harness.SeedSession(trigger.Id, lastActivityUtc: 100);
        }

        var first = await harness.SessionService.ListAsync(new IntegrationSessionFilter(TriggerId: null, Status: null, Limit: 3, Offset: 0)).ConfigureAwait(false);
        var second = await harness.SessionService.ListAsync(new IntegrationSessionFilter(TriggerId: null, Status: null, Limit: 3, Offset: 3)).ConfigureAwait(false);

        var ids = first.Concat(second).Select(static session => session.Id).ToArray();
        AssertEx.Equal(expected: 6, ids.Distinct().Count(), "Two pages of the same size must be disjoint and cover every row.");
        AssertEx.True(first.Select(static session => session.Id).SequenceEqual(first.OrderByDescending(static session => session.Id).Select(static session => session.Id)),
            "Within one activity stamp the id is the tiebreaker, descending.");
    }

    [Test]
    public async Task List_FiltersByStatusAndTriggerServerSide()
    {
        var harness = new IntegrationInvokeHarness();
        var mine = harness.SeedTrigger("mine", sessionPolicy: IntegrationSessionPolicy.CallerManaged);
        var other = harness.SeedTrigger("other", sessionPolicy: IntegrationSessionPolicy.CallerManaged);
        var active = harness.SeedSession(mine.Id);
        _ = harness.SeedSession(mine.Id, status: IntegrationSessionStatus.Closed);
        _ = harness.SeedSession(other.Id);

        var byStatus = await harness.SessionService.ListAsync(new IntegrationSessionFilter(TriggerId: null, IntegrationSessionStatus.Active, Limit: 50, Offset: 0)).ConfigureAwait(false);
        var byTrigger = await harness.SessionService.ListAsync(new IntegrationSessionFilter(mine.Id, Status: null, Limit: 50, Offset: 0)).ConfigureAwait(false);

        AssertEx.Equal(expected: 2, byStatus.Count);
        AssertEx.Equal(expected: 2, byTrigger.Count);
        AssertEx.Contains(byStatus.Select(static session => session.Id), active.Id);
        AssertEx.True(byTrigger.All(session => session.TriggerId == mine.Id));
        AssertEx.True(byTrigger.All(session => string.Equals(session.TriggerName, "mine", StringComparison.Ordinal)),
            "The trigger NAME is what an integrator addresses, so the DTO carries it rather than an id alone.");
    }

    [Test]
    public async Task GetForExternalCaller_ReturnsNullForEveryMaskedCase()
    {
        var harness = new IntegrationInvokeHarness();
        var trigger = harness.SeedTrigger("caller-managed", sessionPolicy: IntegrationSessionPolicy.CallerManaged);
        var sibling = harness.SeedTrigger("sibling", sessionPolicy: IntegrationSessionPolicy.CallerManaged);
        var mine = harness.SeedSession(trigger.Id);
        var foreign = harness.SeedSession(trigger.Id, principalId: Guid.NewGuid());
        var outsideAllowlist = harness.SeedSession(sibling.Id);
        harness.RestrictKeyTo(trigger.Id);

        AssertEx.Null(await harness.SessionService.GetForExternalCallerAsync(Guid.NewGuid(), harness.Caller()).ConfigureAwait(false), "Unknown id.");
        AssertEx.Null(await harness.SessionService.GetForExternalCallerAsync(foreign.Id, harness.Caller()).ConfigureAwait(false), "Another integrator's session.");
        AssertEx.Null(await harness.SessionService.GetForExternalCallerAsync(outsideAllowlist.Id, harness.Caller()).ConfigureAwait(false), "A trigger this key is not scoped to.");

        // The positive control, and the one R4-6 exists for: a DIFFERENT key of the same principal reads it.
        harness.RotateCredential();
        var read = await harness.SessionService.GetForExternalCallerAsync(mine.Id, harness.Caller(IntegrationInvokeHarness.RotatedKeyPrefix)).ConfigureAwait(false);
        AssertEx.Equal(mine.Id, AssertEx.NotNull(read).Id);
        AssertEx.Equal("caller-managed", AssertEx.NotNull(read).TriggerName);
    }

    [Test]
    public async Task Get_ForTheOperator_IsNotPrincipalScoped()
    {
        // The admin surface is deliberately unscoped: an operator reading their own node is not acting as an
        // integrator and must be able to reach every row.
        var harness = new IntegrationInvokeHarness();
        var trigger = harness.SeedTrigger("caller-managed", sessionPolicy: IntegrationSessionPolicy.CallerManaged);
        var foreign = harness.SeedSession(trigger.Id, principalId: Guid.NewGuid());

        var read = await harness.SessionService.GetAsync(foreign.Id).ConfigureAwait(false);

        AssertEx.Equal(foreign.Id, AssertEx.NotNull(read).Id);
        AssertEx.Null(await harness.SessionService.GetAsync(Guid.NewGuid()).ConfigureAwait(false));
    }
}

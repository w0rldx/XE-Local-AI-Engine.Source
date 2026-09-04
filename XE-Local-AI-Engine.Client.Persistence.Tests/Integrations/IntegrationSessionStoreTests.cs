namespace XE_Local_AI_Engine.Client.Persistence.Tests.Integrations;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     The session store's S0 surface, plus the invariant that makes its two missing methods missing on purpose: the
///     session watermark has exactly two writers, and neither of them is a <c>TouchAsync</c>.
/// </summary>
public sealed class IntegrationSessionStoreTests
{
    private static readonly IReadOnlySet<IntegrationExecutionStatus> Accepted = new HashSet<IntegrationExecutionStatus> { IntegrationExecutionStatus.Accepted };

    [Test]
    public async Task ASessionCreatedThroughAcceptAsync_CarriesTheCommandsPrincipalAndConversation()
    {
        using var fixture = new IntegrationTestFixture();
        var seed = await SeedAsync(fixture).ConfigureAwait(false);

        await using var context = fixture.CreateContext();
        var sessionId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        var accept = new IntegrationAcceptCommand(new IntegrationSessionCreate(sessionId, seed.TriggerId, conversationId, seed.AgentDefinitionId),
            executionId,
            seed.TriggerId,
            sessionId,
            seed.PrincipalId,
            Guid.NewGuid(),
            new byte[32],
            seed.KeyPrefix,
            ReceivedAtUtc: 3_000,
            new IntegrationEventAppend(Guid.NewGuid(), executionId, Sequence: 1, "execution.accepted", null, OccurredAtUtc: 3_000));

        AssertEx.True(await new IntegrationExecutionStore(context).AcceptAsync(accept, maxActive: 8, maxActivePerPrincipal: 4).ConfigureAwait(false));

        var session = AssertEx.NotNull(await new IntegrationSessionStore(context).GetByIdAsync(sessionId).ConfigureAwait(false));
        AssertEx.Equal(seed.PrincipalId, session.PrincipalId);
        AssertEx.Equal(conversationId, session.ConversationId,
            "The conversation id is pre-minted before the accept transaction, and the caller creates the conversation at it afterwards.");
        AssertEx.Equal(seed.AgentDefinitionId, session.AgentDefinitionId);
        AssertEx.Equal(IntegrationSessionStatus.Active, session.Status);
        AssertEx.Equal(expected: 1L, session.LastSequence);

        AssertEx.Null(await new IntegrationSessionStore(context).GetByIdAsync(Guid.NewGuid()).ConfigureAwait(false));
    }

    [Test]
    public async Task CloseAsync_IsIdempotentAndAnswersFalseOnlyForAMissingRow()
    {
        using var fixture = new IntegrationTestFixture();
        var seed = await SeedAsync(fixture).ConfigureAwait(false);

        await using var context = fixture.CreateContext();
        var sessionId = await AcceptAsync(context, seed).ConfigureAwait(false);
        var store = new IntegrationSessionStore(context);

        AssertEx.True(await store.CloseAsync(sessionId, atUtc: 4_000).ConfigureAwait(false));
        AssertEx.True(await store.CloseAsync(sessionId, atUtc: 4_100).ConfigureAwait(false), "Closing an already-closed session is a success, not a 404.");
        AssertEx.False(await store.CloseAsync(Guid.NewGuid(), atUtc: 4_200).ConfigureAwait(false));

        var session = AssertEx.NotNull(await store.GetByIdAsync(sessionId).ConfigureAwait(false));
        AssertEx.Equal(IntegrationSessionStatus.Closed, session.Status);
        AssertEx.Equal(expected: 4_100L, session.LastActivityUtc);
    }

    [Test]
    public async Task TheSessionWatermarkHasExactlyTwoWriters_AppendEventAsyncAndTryTerminalizeAsync()
    {
        using var fixture = new IntegrationTestFixture();
        var seed = await SeedAsync(fixture).ConfigureAwait(false);

        await using var context = fixture.CreateContext();
        var executionStore = new IntegrationExecutionStore(context);
        var sessionStore = new IntegrationSessionStore(context);
        var (sessionId, executionId) = await AcceptWithIdsAsync(context, seed).ConfigureAwait(false);

        // Writer one. There is no TouchAsync to test, and there never will be: this is the method that replaces it.
        await executionStore.AppendEventAsync(new IntegrationEventAppend(Guid.NewGuid(), executionId, Sequence: 3, "tool.started", null, OccurredAtUtc: 5_000))
                            .ConfigureAwait(false);
        var afterAppend = AssertEx.NotNull(await sessionStore.GetByIdAsync(sessionId).ConfigureAwait(false));
        AssertEx.Equal(expected: 3L, afterAppend.LastSequence);
        AssertEx.Equal(expected: 5_000L, afterAppend.LastActivityUtc);

        // Writer two, which is why terminalisation can bypass AppendEventAsync without stranding the watermark the UI
        // renders.
        AssertEx.True(await executionStore.TryTerminalizeAsync(new IntegrationTerminalizeCommand(executionId,
                          ExpectedVersion: 0,
                          Accepted,
                          IntegrationExecutionStatus.Completed,
                          Sequence: 4,
                          "execution.completed",
                          EndedAtUtc: 6_000,
                          FailureCategory: null,
                          FailureSummary: null))
                      .ConfigureAwait(false));

        var afterTerminal = AssertEx.NotNull(await sessionStore.GetByIdAsync(sessionId).ConfigureAwait(false));
        AssertEx.Equal(expected: 4L, afterTerminal.LastSequence);
        AssertEx.Equal(expected: 6_000L, afterTerminal.LastActivityUtc);
    }

    [Test]
    public async Task GetForPrincipalAsync_AnswersNullForAMissingRowAndForAForeignOne()
    {
        // The masking rule is the SHAPE of the return, not an if a route has to remember: a missing session and
        // another integrator's session must be one non-result.
        using var fixture = new IntegrationTestFixture();
        var seed = await SeedAsync(fixture).ConfigureAwait(false);

        await using var context = fixture.CreateContext();
        var sessionId = await AcceptAsync(context, seed).ConfigureAwait(false);
        var store = new IntegrationSessionStore(context);

        _ = AssertEx.NotNull(await store.GetForPrincipalAsync(sessionId, seed.PrincipalId).ConfigureAwait(false));
        AssertEx.Null(await store.GetForPrincipalAsync(sessionId, Guid.NewGuid()).ConfigureAwait(false), "Another integrator's session.");
        AssertEx.Null(await store.GetForPrincipalAsync(Guid.NewGuid(), seed.PrincipalId).ConfigureAwait(false), "An unknown id.");
    }

    [Test]
    public async Task FindByConversationAsync_ResolvesTheSessionThatOwnsAConversation()
    {
        // The lookup behind emit_output: a tool handler holds only the ambient conversation id the runner seeded.
        using var fixture = new IntegrationTestFixture();
        var seed = await SeedAsync(fixture).ConfigureAwait(false);

        await using var context = fixture.CreateContext();
        var sessionId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        var accept = new IntegrationAcceptCommand(new IntegrationSessionCreate(sessionId, seed.TriggerId, conversationId, seed.AgentDefinitionId),
            executionId,
            seed.TriggerId,
            sessionId,
            seed.PrincipalId,
            Guid.NewGuid(),
            new byte[32],
            seed.KeyPrefix,
            ReceivedAtUtc: 3_000,
            new IntegrationEventAppend(Guid.NewGuid(), executionId, Sequence: 1, "execution.accepted", null, OccurredAtUtc: 3_000));
        AssertEx.True(await new IntegrationExecutionStore(context).AcceptAsync(accept, maxActive: 8, maxActivePerPrincipal: 4).ConfigureAwait(false));

        var store = new IntegrationSessionStore(context);
        AssertEx.Equal(sessionId, AssertEx.NotNull(await store.FindByConversationAsync(conversationId).ConfigureAwait(false)).Id);
        AssertEx.Null(await store.FindByConversationAsync(Guid.NewGuid()).ConfigureAwait(false),
            "Every non-integration conversation resolves to nothing, which is what makes emit_output inert outside an integration run.");
    }

    [Test]
    public async Task ListAsync_OrdersByLastActivityThenIdBeforePagingAndFiltersServerSide()
    {
        // Duplicate activity stamps are exactly the case an unordered Skip/Take drops or duplicates rows across pages.
        using var fixture = new IntegrationTestFixture();
        await using (var schema = await fixture.CreateSchemaAsync().ConfigureAwait(false))
        {
            var trigger = IntegrationTestFixture.Trigger();
            var other = IntegrationTestFixture.Trigger("other-feed");
            schema.IntegrationTriggers.AddRange(trigger, other);
            var principalId = Guid.NewGuid();
            for (var i = 0; i < 5; i++)
            {
                _ = schema.IntegrationSessions.Add(IntegrationTestFixture.Session(trigger.Id, principalId, lastActivityUtc: 7_000));
            }

            var closed = IntegrationTestFixture.Session(trigger.Id, principalId, lastActivityUtc: 9_000);
            closed.Status = IntegrationSessionStatus.Closed;
            _ = schema.IntegrationSessions.Add(closed);
            _ = schema.IntegrationSessions.Add(IntegrationTestFixture.Session(other.Id, principalId, lastActivityUtc: 8_000));
            _ = await schema.SaveChangesAsync().ConfigureAwait(false);

            var store = new IntegrationSessionStore(schema);
            var first = await store.ListAsync(triggerId: null, status: null, limit: 3, offset: 0).ConfigureAwait(false);
            var second = await store.ListAsync(triggerId: null, status: null, limit: 3, offset: 3).ConfigureAwait(false);
            var third = await store.ListAsync(triggerId: null, status: null, limit: 3, offset: 6).ConfigureAwait(false);

            var ids = first.Concat(second).Concat(third).Select(static session => session.Id).ToArray();
            AssertEx.Equal(expected: 7, ids.Length);
            AssertEx.Equal(expected: 7, ids.Distinct().Count(), "Three pages must be disjoint and cover every row.");
            AssertEx.Equal(closed.Id, first[0].Id, "Newest activity leads.");
            AssertEx.True(ids.SequenceEqual(ids.Distinct()), "The id tiebreaker makes duplicate stamps page deterministically.");

            var byTrigger = await store.ListAsync(other.Id, status: null, limit: 50, offset: 0).ConfigureAwait(false);
            var byStatus = await store.ListAsync(triggerId: null, IntegrationSessionStatus.Closed, limit: 50, offset: 0).ConfigureAwait(false);
            AssertEx.Equal(expected: 1, byTrigger.Count);
            AssertEx.Equal(expected: 1, byStatus.Count);
            AssertEx.Equal(closed.Id, byStatus[0].Id);
        }
    }

    [Test]
    public async Task DeleteAsync_RemovesTheRowAndIsFalseWhenThePurgeAlreadyDidIt()
    {
        using var fixture = new IntegrationTestFixture();
        var seed = await SeedAsync(fixture).ConfigureAwait(false);

        await using var context = fixture.CreateContext();
        var sessionId = await AcceptAsync(context, seed).ConfigureAwait(false);
        var store = new IntegrationSessionStore(context);

        AssertEx.True(await store.DeleteAsync(sessionId).ConfigureAwait(false));
        AssertEx.False(await store.DeleteAsync(sessionId).ConfigureAwait(false),
            "The conversation purge is the mechanism; this is the backstop, so a second call is the ordinary no-op.");
        AssertEx.Null(await store.GetByIdAsync(sessionId).ConfigureAwait(false));
    }

    private static async Task<Guid> AcceptAsync(NodeChatDbContext context, SeedState seed) =>
        (await AcceptWithIdsAsync(context, seed).ConfigureAwait(false)).SessionId;

    private static async Task<(Guid SessionId, Guid ExecutionId)> AcceptWithIdsAsync(NodeChatDbContext context, SeedState seed)
    {
        var sessionId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        var accept = new IntegrationAcceptCommand(new IntegrationSessionCreate(sessionId, seed.TriggerId, Guid.NewGuid(), seed.AgentDefinitionId),
            executionId,
            seed.TriggerId,
            sessionId,
            seed.PrincipalId,
            Guid.NewGuid(),
            new byte[32],
            seed.KeyPrefix,
            ReceivedAtUtc: 3_000,
            new IntegrationEventAppend(Guid.NewGuid(), executionId, Sequence: 1, "execution.accepted", null, OccurredAtUtc: 3_000));

        AssertEx.True(await new IntegrationExecutionStore(context).AcceptAsync(accept, maxActive: 8, maxActivePerPrincipal: 4).ConfigureAwait(false));
        return (sessionId, executionId);
    }

    private static async Task<SeedState> SeedAsync(IntegrationTestFixture fixture)
    {
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var trigger = IntegrationTestFixture.Trigger();
        var key = IntegrationTestFixture.ApiKey();
        _ = context.IntegrationTriggers.Add(trigger);
        _ = context.IntegrationApiKeys.Add(key);
        _ = await context.SaveChangesAsync().ConfigureAwait(false);
        return new SeedState(trigger.Id, trigger.TargetAgentDefinitionId, key.PrincipalId, key.KeyPrefix);
    }

    private sealed record SeedState(Guid TriggerId, Guid AgentDefinitionId, Guid PrincipalId, string KeyPrefix);
}

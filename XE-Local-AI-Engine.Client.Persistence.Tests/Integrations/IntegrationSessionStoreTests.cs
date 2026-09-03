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

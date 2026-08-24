namespace XE_Local_AI_Engine.Client.Persistence.Tests.WorkSessions;

using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

public sealed class AgentWorkSessionStatusTransitionTests
{
    private static readonly (AgentWorkSessionStatus From, AgentWorkSessionStatus To)[] LegalTransitions =
    [
        (AgentWorkSessionStatus.Draft, AgentWorkSessionStatus.Running),
        (AgentWorkSessionStatus.Draft, AgentWorkSessionStatus.Cancelled),
        (AgentWorkSessionStatus.Running, AgentWorkSessionStatus.Paused),
        (AgentWorkSessionStatus.Running, AgentWorkSessionStatus.WaitingForInput),
        (AgentWorkSessionStatus.Running, AgentWorkSessionStatus.WaitingForApproval),
        (AgentWorkSessionStatus.Running, AgentWorkSessionStatus.Completed),
        (AgentWorkSessionStatus.Running, AgentWorkSessionStatus.Failed),
        (AgentWorkSessionStatus.Running, AgentWorkSessionStatus.Cancelled),
        (AgentWorkSessionStatus.Paused, AgentWorkSessionStatus.Running),
        (AgentWorkSessionStatus.Paused, AgentWorkSessionStatus.Cancelled),
        (AgentWorkSessionStatus.WaitingForInput, AgentWorkSessionStatus.Running),
        (AgentWorkSessionStatus.WaitingForInput, AgentWorkSessionStatus.Paused),
        (AgentWorkSessionStatus.WaitingForInput, AgentWorkSessionStatus.Cancelled),
        (AgentWorkSessionStatus.WaitingForApproval, AgentWorkSessionStatus.Running),
        (AgentWorkSessionStatus.WaitingForApproval, AgentWorkSessionStatus.Paused),
        (AgentWorkSessionStatus.WaitingForApproval, AgentWorkSessionStatus.Cancelled),
        (AgentWorkSessionStatus.Interrupted, AgentWorkSessionStatus.Running),
        (AgentWorkSessionStatus.Interrupted, AgentWorkSessionStatus.Paused),
        (AgentWorkSessionStatus.Interrupted, AgentWorkSessionStatus.Failed),
        (AgentWorkSessionStatus.Interrupted, AgentWorkSessionStatus.Cancelled)
    ];

    private static readonly (AgentWorkSessionStatus From, AgentWorkSessionStatus To)[] IllegalTransitions =
    [
        (AgentWorkSessionStatus.Completed, AgentWorkSessionStatus.Running),
        (AgentWorkSessionStatus.Draft, AgentWorkSessionStatus.Paused),
        (AgentWorkSessionStatus.Cancelled, AgentWorkSessionStatus.Running),
        (AgentWorkSessionStatus.Failed, AgentWorkSessionStatus.Running),
        (AgentWorkSessionStatus.Paused, AgentWorkSessionStatus.Completed)
    ];

    [Test]
    public async Task EveryDeclaredTransition_IsAccepted()
    {
        using var fixture = new WorkSessionTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = WorkSessionTestFixture.StoreFor(context);

        foreach (var (from, to) in LegalTransitions)
        {
            var sessionId = Guid.NewGuid();
            var version = await ArrangeAsync(store, context, sessionId, from).ConfigureAwait(false);
            var moved = await store.TransitionStatusAsync(new TransitionWorkSessionStatusCommand(sessionId, version, to)).ConfigureAwait(false);
            AssertEx.Equal(to, moved.Status, $"{from} -> {to} must be accepted.");
        }
    }

    [Test]
    public async Task IllegalTransitions_AreRefused()
    {
        using var fixture = new WorkSessionTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = WorkSessionTestFixture.StoreFor(context);

        foreach (var (from, to) in IllegalTransitions)
        {
            var sessionId = Guid.NewGuid();
            var version = await ArrangeAsync(store, context, sessionId, from).ConfigureAwait(false);
            _ = await AssertEx.ThrowsAsync<WorkSessionInvalidTransitionException>(() =>
                                  store.TransitionStatusAsync(new TransitionWorkSessionStatusCommand(sessionId, version, to)),
                              $"{from} -> {to} must be refused.")
                              .ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Interrupted_IsNotWritableByALiveCaller()
    {
        using var fixture = new WorkSessionTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = WorkSessionTestFixture.StoreFor(context);
        var sessionId = Guid.NewGuid();
        var version = await ArrangeAsync(store, context, sessionId, AgentWorkSessionStatus.Running).ConfigureAwait(false);

        // Only the startup reconcile records a host that died; a live caller asserting it would be a lie.
        _ = await AssertEx.ThrowsAsync<WorkSessionInvalidTransitionException>(() =>
                              store.TransitionStatusAsync(new TransitionWorkSessionStatusCommand(sessionId, version, AgentWorkSessionStatus.Interrupted)))
                          .ConfigureAwait(false);
    }

    [Test]
    public async Task ParkedSession_DemotesToPausedWithNoOperatorInvolvement()
    {
        using var fixture = new WorkSessionTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = WorkSessionTestFixture.StoreFor(context);
        var sessionId = Guid.NewGuid();
        var version = await ArrangeAsync(store, context, sessionId, AgentWorkSessionStatus.WaitingForApproval).ConfigureAwait(false);

        // Park expiry is a supervisor action, not a human one: an unattended parked session must be able to release the
        // node's single invocation slot on its own.
        var paused = await store.TransitionStatusAsync(new TransitionWorkSessionStatusCommand(sessionId,
                version,
                AgentWorkSessionStatus.Paused,
                SanitizedReason: "The approval went unanswered past the configured budget."))
            .ConfigureAwait(false);
        AssertEx.Equal(AgentWorkSessionStatus.Paused, paused.Status);
    }

    [Test]
    public async Task StaleVersion_FailsAContentWriteButNotASentinelStatusWrite()
    {
        using var fixture = new WorkSessionTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = WorkSessionTestFixture.StoreFor(context);
        var sessionId = Guid.NewGuid();

        var created = await WorkSessionTestFixture.SeedAsync(store, sessionId).ConfigureAwait(false);
        var stale = created.Version;
        _ = await store.AppendEventAsync(new AppendWorkSessionEventCommand(sessionId, stale, "MovesTheVersionOn")).ConfigureAwait(false);

        _ = await AssertEx.ThrowsAsync<WorkSessionConcurrencyException>(() => store.AppendFindingAsync(new AppendWorkSessionFindingCommand(sessionId,
                              Guid.NewGuid(),
                              stale,
                              Guid.NewGuid(),
                              AgentWorkSessionFindingKind.Finding,
                              "Lost update."))).ConfigureAwait(false);

        var moved = await store.TransitionStatusAsync(new TransitionWorkSessionStatusCommand(sessionId, WorkSessionVersions.Any, AgentWorkSessionStatus.Running))
                               .ConfigureAwait(false);
        AssertEx.Equal(AgentWorkSessionStatus.Running, moved.Status);
    }

    [Test]
    public async Task TerminalTransition_ClearsTheCurrentTask()
    {
        using var fixture = new WorkSessionTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = WorkSessionTestFixture.StoreFor(context);
        var sessionId = Guid.NewGuid();
        var taskId = Guid.NewGuid();

        var created = await WorkSessionTestFixture.SeedAsync(store, sessionId).ConfigureAwait(false);
        var planned = await store.ApplyPlanAsync(new ApplyWorkPlanCommand(sessionId,
                created.Version,
                Guid.NewGuid(),
                AgentWorkSessionTaskOrigin.Agent,
                [new WorkPlanTaskChange(taskId, WorkPlanTaskOperation.Add, Title: "Current")]))
            .ConfigureAwait(false);
        var running = await store.TransitionStatusAsync(new TransitionWorkSessionStatusCommand(sessionId, planned.Version, AgentWorkSessionStatus.Running, taskId))
                                 .ConfigureAwait(false);
        AssertEx.Equal(taskId, running.CurrentTaskId);

        var completed = await store.TransitionStatusAsync(new TransitionWorkSessionStatusCommand(sessionId, running.Version, AgentWorkSessionStatus.Completed))
                                   .ConfigureAwait(false);
        AssertEx.Null(completed.CurrentTaskId, "A terminal session must not keep pointing at a current task.");
    }

    /// <summary>
    ///     Puts a fresh session into <paramref name="status" /> and answers the version to write against. Terminal and
    ///     <c>Interrupted</c> states are seeded straight onto the row, because no legal transition reaches them from a
    ///     caller.
    /// </summary>
    private static async Task<long> ArrangeAsync(AgentWorkSessionStore store, NodeChatDbContext context, Guid sessionId, AgentWorkSessionStatus status)
    {
        var created = await WorkSessionTestFixture.SeedAsync(store, sessionId).ConfigureAwait(false);
        if (status == AgentWorkSessionStatus.Draft)
        {
            return created.Version;
        }

        if (status is AgentWorkSessionStatus.Interrupted or AgentWorkSessionStatus.Completed or AgentWorkSessionStatus.Failed or AgentWorkSessionStatus.Cancelled)
        {
            var entity = await context.AgentWorkSessions.SingleAsync(candidate => candidate.Id == sessionId).ConfigureAwait(false);
            entity.Status = status;
            entity.Version++;
            _ = await context.SaveChangesAsync().ConfigureAwait(false);
            context.ChangeTracker.Clear();
            return entity.Version;
        }

        var running = await store.TransitionStatusAsync(new TransitionWorkSessionStatusCommand(sessionId, created.Version, AgentWorkSessionStatus.Running))
                                 .ConfigureAwait(false);
        return status == AgentWorkSessionStatus.Running
            ? running.Version
            : (await store.TransitionStatusAsync(new TransitionWorkSessionStatusCommand(sessionId, running.Version, status)).ConfigureAwait(false)).Version;
    }
}

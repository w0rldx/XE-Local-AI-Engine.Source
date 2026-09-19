namespace XE_Local_AI_Engine.Client.Persistence.Tests.WorkSessions;

using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

[Category(TestCategories.Integration)]
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
        await using var context = await fixture.CreateSchemaAsync();
        var store = WorkSessionTestFixture.StoreFor(context);

        foreach (var (from, to) in LegalTransitions)
        {
            var sessionId = Guid.NewGuid();
            var version = await ArrangeAsync(store, context, sessionId, from);
            var moved = await store.TransitionStatusAsync(new TransitionWorkSessionStatusCommand { SessionId = sessionId, ExpectedVersion = version, TargetStatus = to });
            AssertEx.Equal(to, moved.Status, $"{from} -> {to} must be accepted.");
        }
    }

    [Test]
    public async Task IllegalTransitions_AreRefused()
    {
        using var fixture = new WorkSessionTestFixture();
        await using var context = await fixture.CreateSchemaAsync();
        var store = WorkSessionTestFixture.StoreFor(context);

        foreach (var (from, to) in IllegalTransitions)
        {
            var sessionId = Guid.NewGuid();
            var version = await ArrangeAsync(store, context, sessionId, from);
            _ = await AssertEx.ThrowsAsync<WorkSessionInvalidTransitionException>(() =>
                                      store.TransitionStatusAsync(new TransitionWorkSessionStatusCommand { SessionId = sessionId, ExpectedVersion = version, TargetStatus = to }),
                                  $"{from} -> {to} must be refused.");
        }
    }

    [Test]
    public async Task Interrupted_IsNotWritableByALiveCaller()
    {
        using var fixture = new WorkSessionTestFixture();
        await using var context = await fixture.CreateSchemaAsync();
        var store = WorkSessionTestFixture.StoreFor(context);
        var sessionId = Guid.NewGuid();
        var version = await ArrangeAsync(store, context, sessionId, AgentWorkSessionStatus.Running);

        // Only the startup reconcile records a host that died; a live caller asserting it would be a lie.
        _ = await AssertEx.ThrowsAsync<WorkSessionInvalidTransitionException>(() =>
                              store.TransitionStatusAsync(new TransitionWorkSessionStatusCommand { SessionId = sessionId, ExpectedVersion = version, TargetStatus = AgentWorkSessionStatus.Interrupted }));
    }

    [Test]
    public async Task ParkedSession_DemotesToPausedWithNoOperatorInvolvement()
    {
        using var fixture = new WorkSessionTestFixture();
        await using var context = await fixture.CreateSchemaAsync();
        var store = WorkSessionTestFixture.StoreFor(context);
        var sessionId = Guid.NewGuid();
        var version = await ArrangeAsync(store, context, sessionId, AgentWorkSessionStatus.WaitingForApproval);

        // Park expiry is a supervisor action, not a human one: an unattended parked session must be able to release the
        // node's single invocation slot on its own.
        var paused = await store.TransitionStatusAsync(new TransitionWorkSessionStatusCommand
        {
            SessionId = sessionId,
            ExpectedVersion = version,
            TargetStatus = AgentWorkSessionStatus.Paused,
            SanitizedReason = "The approval went unanswered past the configured budget."
        });
        AssertEx.Equal(AgentWorkSessionStatus.Paused, paused.Status);
    }

    [Test]
    public async Task StaleVersion_FailsAContentWriteButNotASentinelStatusWrite()
    {
        using var fixture = new WorkSessionTestFixture();
        await using var context = await fixture.CreateSchemaAsync();
        var store = WorkSessionTestFixture.StoreFor(context);
        var sessionId = Guid.NewGuid();

        var created = await WorkSessionTestFixture.SeedAsync(store, sessionId);
        var stale = created.Version;
        _ = await store.AppendEventAsync(new AppendWorkSessionEventCommand { SessionId = sessionId, ExpectedVersion = stale, EventType = "MovesTheVersionOn" });

        _ = await AssertEx.ThrowsAsync<WorkSessionConcurrencyException>(() => store.AppendFindingAsync(new AppendWorkSessionFindingCommand
        {
            SessionId = sessionId,
            FindingId = Guid.NewGuid(),
            ExpectedVersion = stale,
            OperationId = Guid.NewGuid(),
            Kind = AgentWorkSessionFindingKind.Finding,
            Text = "Lost update."
        }));

        var moved = await store.TransitionStatusAsync(new TransitionWorkSessionStatusCommand { SessionId = sessionId, ExpectedVersion = WorkSessionVersions.Any, TargetStatus = AgentWorkSessionStatus.Running });
        AssertEx.Equal(AgentWorkSessionStatus.Running, moved.Status);
    }

    [Test]
    public async Task TerminalTransition_ClearsTheCurrentTask()
    {
        using var fixture = new WorkSessionTestFixture();
        await using var context = await fixture.CreateSchemaAsync();
        var store = WorkSessionTestFixture.StoreFor(context);
        var sessionId = Guid.NewGuid();
        var taskId = Guid.NewGuid();

        var created = await WorkSessionTestFixture.SeedAsync(store, sessionId);
        var planned = await store.ApplyPlanAsync(new ApplyWorkPlanCommand
        {
            SessionId = sessionId,
            ExpectedVersion = created.Version,
            OperationId = Guid.NewGuid(),
            Origin = AgentWorkSessionTaskOrigin.Agent,
            Changes = [new WorkPlanTaskChange { TaskId = taskId, Operation = WorkPlanTaskOperation.Add, Title = "Current" }]
        });
        var running = await store.TransitionStatusAsync(new TransitionWorkSessionStatusCommand { SessionId = sessionId, ExpectedVersion = planned.Version, TargetStatus = AgentWorkSessionStatus.Running, CurrentTaskId = taskId });
        AssertEx.Equal(taskId, running.CurrentTaskId);

        var completed = await store.TransitionStatusAsync(new TransitionWorkSessionStatusCommand { SessionId = sessionId, ExpectedVersion = running.Version, TargetStatus = AgentWorkSessionStatus.Completed });
        AssertEx.Null(completed.CurrentTaskId, "A terminal session must not keep pointing at a current task.");
    }

    /// <summary>
    ///     Puts a fresh session into <paramref name="status" /> and answers the version to write against. Terminal and
    ///     <c>Interrupted</c> states are seeded straight onto the row, because no legal transition reaches them from a
    ///     caller.
    /// </summary>
    private static async Task<long> ArrangeAsync(AgentWorkSessionStore store, NodeChatDbContext context, Guid sessionId, AgentWorkSessionStatus status)
    {
        var created = await WorkSessionTestFixture.SeedAsync(store, sessionId);
        if (status == AgentWorkSessionStatus.Draft)
        {
            return created.Version;
        }

        if (status is AgentWorkSessionStatus.Interrupted or AgentWorkSessionStatus.Completed or AgentWorkSessionStatus.Failed or AgentWorkSessionStatus.Cancelled)
        {
            var entity = await context.AgentWorkSessions.SingleAsync(candidate => candidate.Id == sessionId);
            entity.Status = status;
            entity.Version++;
            _ = await context.SaveChangesAsync();
            context.ChangeTracker.Clear();
            return entity.Version;
        }

        var running = await store.TransitionStatusAsync(new TransitionWorkSessionStatusCommand { SessionId = sessionId, ExpectedVersion = created.Version, TargetStatus = AgentWorkSessionStatus.Running });
        return status == AgentWorkSessionStatus.Running
            ? running.Version
            : (await store.TransitionStatusAsync(new TransitionWorkSessionStatusCommand { SessionId = sessionId, ExpectedVersion = running.Version, TargetStatus = status })).Version;
    }
}

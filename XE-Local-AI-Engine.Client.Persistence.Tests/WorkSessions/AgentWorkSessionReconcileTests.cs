namespace XE_Local_AI_Engine.Client.Persistence.Tests.WorkSessions;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

[Category(TestCategories.Integration)]
public sealed class AgentWorkSessionReconcileTests
{
    private const string Reason = "The host restarted while the work session was in flight.";

    [Test]
    public async Task Reconcile_CollapsesEveryInFlightStateAndLeavesTheRestAlone()
    {
        using var fixture = new WorkSessionTestFixture();
        await using var context = await fixture.CreateSchemaAsync();
        var store = WorkSessionTestFixture.StoreFor(context);

        // WaitingForInput is where a session sits when the engine dies mid-ask_user, so it is the resume path's entry
        // point and has to collapse too.
        var running = await ArrangeAsync(store, AgentWorkSessionStatus.Running);
        var waitingForApproval = await ArrangeAsync(store, AgentWorkSessionStatus.WaitingForApproval);
        var waitingForInput = await ArrangeAsync(store, AgentWorkSessionStatus.WaitingForInput);
        var paused = await ArrangeAsync(store, AgentWorkSessionStatus.Paused);
        var completed = await ArrangeAsync(store, AgentWorkSessionStatus.Completed);
        var draft = await ArrangeAsync(store, AgentWorkSessionStatus.Draft);

        AssertEx.Equal(expected: 3, await store.ReconcileRunningSessionsAsync(Reason));

        foreach (var sessionId in new[]
                 {
                     running,
                     waitingForApproval,
                     waitingForInput
                 })
        {
            var session = await store.GetAsync(sessionId);
            AssertEx.Equal(AgentWorkSessionStatus.Interrupted, session.Status);
            var interruptEvents = (await store.ListEventsAsync(sessionId)).Where(entry => entry.EventType == "SessionInterrupted").ToArray();
            AssertEx.Equal(expected: 1, interruptEvents.Length, "Each collapsed session must record exactly one reconcile event.");
            AssertEx.True(AssertEx.NotNull(interruptEvents[0].DetailJson).Contains(Reason, StringComparison.Ordinal), "The reconcile event must carry the sanitized reason.");
        }

        AssertEx.Equal(AgentWorkSessionStatus.Paused, (await store.GetAsync(paused)).Status);
        AssertEx.Equal(AgentWorkSessionStatus.Completed, (await store.GetAsync(completed)).Status);
        AssertEx.Equal(AgentWorkSessionStatus.Draft, (await store.GetAsync(draft)).Status);
    }

    [Test]
    public async Task Reconcile_IsANoOpOnASecondPass()
    {
        using var fixture = new WorkSessionTestFixture();
        await using var context = await fixture.CreateSchemaAsync();
        var store = WorkSessionTestFixture.StoreFor(context);
        var sessionId = await ArrangeAsync(store, AgentWorkSessionStatus.Running);

        AssertEx.Equal(expected: 1, await store.ReconcileRunningSessionsAsync(Reason));
        AssertEx.Equal(expected: 0, await store.ReconcileRunningSessionsAsync(Reason));
        AssertEx.Equal(expected: 1,
            (await store.ListEventsAsync(sessionId)).Count(entry => entry.EventType == "SessionInterrupted"));
    }

    [Test]
    public async Task InterruptedSession_ResumesToRunning()
    {
        using var fixture = new WorkSessionTestFixture();
        await using var context = await fixture.CreateSchemaAsync();
        var store = WorkSessionTestFixture.StoreFor(context);
        var sessionId = await ArrangeAsync(store, AgentWorkSessionStatus.WaitingForInput);
        _ = await store.ReconcileRunningSessionsAsync(Reason);

        var interrupted = await store.GetAsync(sessionId);
        var resumed = await store.TransitionStatusAsync(new TransitionWorkSessionStatusCommand(sessionId, interrupted.Version, AgentWorkSessionStatus.Running));
        AssertEx.Equal(AgentWorkSessionStatus.Running, resumed.Status);
    }

    private static async Task<Guid> ArrangeAsync(AgentWorkSessionStore store, AgentWorkSessionStatus status)
    {
        var sessionId = Guid.NewGuid();
        var created = await WorkSessionTestFixture.SeedAsync(store, sessionId);
        if (status == AgentWorkSessionStatus.Draft)
        {
            return sessionId;
        }

        var running = await store.TransitionStatusAsync(new TransitionWorkSessionStatusCommand(sessionId, created.Version, AgentWorkSessionStatus.Running));
        if (status != AgentWorkSessionStatus.Running)
        {
            _ = await store.TransitionStatusAsync(new TransitionWorkSessionStatusCommand(sessionId, running.Version, status));
        }

        return sessionId;
    }
}

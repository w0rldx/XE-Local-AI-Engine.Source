namespace XE_Local_AI_Engine.Client.Persistence.Tests.WorkSessions;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

public sealed class AgentWorkSessionReconcileTests
{
    private const string Reason = "The host restarted while the work session was in flight.";

    [Test]
    public async Task Reconcile_CollapsesEveryInFlightStateAndLeavesTheRestAlone()
    {
        using var fixture = new WorkSessionTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = WorkSessionTestFixture.StoreFor(context);

        // WaitingForInput is where a session sits when the engine dies mid-ask_user, so it is the resume path's entry
        // point and has to collapse too.
        var running = await ArrangeAsync(store, AgentWorkSessionStatus.Running).ConfigureAwait(false);
        var waitingForApproval = await ArrangeAsync(store, AgentWorkSessionStatus.WaitingForApproval).ConfigureAwait(false);
        var waitingForInput = await ArrangeAsync(store, AgentWorkSessionStatus.WaitingForInput).ConfigureAwait(false);
        var paused = await ArrangeAsync(store, AgentWorkSessionStatus.Paused).ConfigureAwait(false);
        var completed = await ArrangeAsync(store, AgentWorkSessionStatus.Completed).ConfigureAwait(false);
        var draft = await ArrangeAsync(store, AgentWorkSessionStatus.Draft).ConfigureAwait(false);

        AssertEx.Equal(expected: 3, await store.ReconcileRunningSessionsAsync(Reason).ConfigureAwait(false));

        foreach (var sessionId in new[] { running, waitingForApproval, waitingForInput })
        {
            var session = await store.GetAsync(sessionId).ConfigureAwait(false);
            AssertEx.Equal(AgentWorkSessionStatus.Interrupted, session.Status);
            var interruptEvents = (await store.ListEventsAsync(sessionId).ConfigureAwait(false)).Where(entry => entry.EventType == "SessionInterrupted").ToArray();
            AssertEx.Equal(expected: 1, interruptEvents.Length, "Each collapsed session must record exactly one reconcile event.");
            AssertEx.True(AssertEx.NotNull(interruptEvents[0].DetailJson).Contains(Reason, StringComparison.Ordinal), "The reconcile event must carry the sanitized reason.");
        }

        AssertEx.Equal(AgentWorkSessionStatus.Paused, (await store.GetAsync(paused).ConfigureAwait(false)).Status);
        AssertEx.Equal(AgentWorkSessionStatus.Completed, (await store.GetAsync(completed).ConfigureAwait(false)).Status);
        AssertEx.Equal(AgentWorkSessionStatus.Draft, (await store.GetAsync(draft).ConfigureAwait(false)).Status);
    }

    [Test]
    public async Task Reconcile_IsANoOpOnASecondPass()
    {
        using var fixture = new WorkSessionTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = WorkSessionTestFixture.StoreFor(context);
        var sessionId = await ArrangeAsync(store, AgentWorkSessionStatus.Running).ConfigureAwait(false);

        AssertEx.Equal(expected: 1, await store.ReconcileRunningSessionsAsync(Reason).ConfigureAwait(false));
        AssertEx.Equal(expected: 0, await store.ReconcileRunningSessionsAsync(Reason).ConfigureAwait(false));
        AssertEx.Equal(expected: 1,
            (await store.ListEventsAsync(sessionId).ConfigureAwait(false)).Count(entry => entry.EventType == "SessionInterrupted"));
    }

    [Test]
    public async Task InterruptedSession_ResumesToRunning()
    {
        using var fixture = new WorkSessionTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = WorkSessionTestFixture.StoreFor(context);
        var sessionId = await ArrangeAsync(store, AgentWorkSessionStatus.WaitingForInput).ConfigureAwait(false);
        _ = await store.ReconcileRunningSessionsAsync(Reason).ConfigureAwait(false);

        var interrupted = await store.GetAsync(sessionId).ConfigureAwait(false);
        var resumed = await store.TransitionStatusAsync(new TransitionWorkSessionStatusCommand(sessionId, interrupted.Version, AgentWorkSessionStatus.Running))
                                 .ConfigureAwait(false);
        AssertEx.Equal(AgentWorkSessionStatus.Running, resumed.Status);
    }

    private static async Task<Guid> ArrangeAsync(AgentWorkSessionStore store, AgentWorkSessionStatus status)
    {
        var sessionId = Guid.NewGuid();
        var created = await WorkSessionTestFixture.SeedAsync(store, sessionId).ConfigureAwait(false);
        if (status == AgentWorkSessionStatus.Draft)
        {
            return sessionId;
        }

        var running = await store.TransitionStatusAsync(new TransitionWorkSessionStatusCommand(sessionId, created.Version, AgentWorkSessionStatus.Running))
                                 .ConfigureAwait(false);
        if (status != AgentWorkSessionStatus.Running)
        {
            _ = await store.TransitionStatusAsync(new TransitionWorkSessionStatusCommand(sessionId, running.Version, status)).ConfigureAwait(false);
        }

        return sessionId;
    }
}

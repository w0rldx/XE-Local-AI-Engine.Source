namespace XE_Local_AI_Engine.Client.Persistence.Tests.WorkSessions;

using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

[Category(TestCategories.Integration)]
public sealed class AgentWorkSessionSequenceTests
{
    [Test]
    public async Task Sequence_IncreasesStrictlyAcrossMixedAppendKindsWithNoGaps()
    {
        using var fixture = new WorkSessionTestFixture();
        await using var context = await fixture.CreateSchemaAsync();
        var store = WorkSessionTestFixture.StoreFor(context);
        var sessionId = Guid.NewGuid();
        var taskId = Guid.NewGuid();

        var created = await WorkSessionTestFixture.SeedAsync(store, sessionId);
        var planned = await store.ApplyPlanAsync(new ApplyWorkPlanCommand(sessionId,
                                     created.Version,
                                     Guid.NewGuid(),
                                     AgentWorkSessionTaskOrigin.Agent,
                                     [new WorkPlanTaskChange(taskId, WorkPlanTaskOperation.Add, Title: "Only task")]));
        var found = await store.AppendFindingAsync(new AppendWorkSessionFindingCommand(sessionId,
                                   Guid.NewGuid(),
                                   planned.Version,
                                   Guid.NewGuid(),
                                   AgentWorkSessionFindingKind.Evidence,
                                   "Evidence."));
        var stepped = await store.AdvanceStepAsync(sessionId, found.Version);
        var checkpointed = await store.AppendCheckpointAsync(new AppendWorkSessionCheckpointCommand(sessionId,
                                          Guid.NewGuid(),
                                          stepped.Version,
                                          Guid.NewGuid(),
                                          stepped.Step,
                                          "Summary.",
                                          "{}"));

        AssertEx.True(created.LastSequence < planned.Sequence, "The plan event must follow the create event.");
        AssertEx.True(planned.Sequence < found.Sequence, "The finding event must follow the plan event.");
        AssertEx.True(found.Sequence < stepped.Sequence, "The step event must follow the finding event.");
        AssertEx.True(stepped.Sequence < checkpointed.Sequence, "The checkpoint event must follow the step event.");

        var allocated = await AllocatedSequencesAsync(context, sessionId);
        var session = await store.GetAsync(sessionId);
        AssertEx.Equal(session.LastSequence, allocated[^1]);
        AssertEx.True(allocated.SequenceEqual(Enumerable.Range(start: 1, allocated.Count).Select(value => (long)value)),
            $"Every allocation must be contiguous from 1; got [{string.Join(", ", allocated)}].");
    }

    [Test]
    public async Task TaskUpdate_ReStampsTheWatermarkWithoutReorderingTheDisplayList()
    {
        using var fixture = new WorkSessionTestFixture();
        await using var context = await fixture.CreateSchemaAsync();
        var store = WorkSessionTestFixture.StoreFor(context);
        var sessionId = Guid.NewGuid();
        var firstTask = Guid.NewGuid();
        var secondTask = Guid.NewGuid();

        var created = await WorkSessionTestFixture.SeedAsync(store, sessionId);
        var planned = await store.ApplyPlanAsync(new ApplyWorkPlanCommand(sessionId,
                                     created.Version,
                                     Guid.NewGuid(),
                                     AgentWorkSessionTaskOrigin.Agent,
                                     [
                                         new WorkPlanTaskChange(firstTask, WorkPlanTaskOperation.Add, Title: "First"),
                                         new WorkPlanTaskChange(secondTask, WorkPlanTaskOperation.Add, Title: "Second")
                                     ]));

        var beforeUpdate = await store.ListTasksAsync(sessionId);
        var displayOrder = beforeUpdate.Select(task => task.Id).ToArray();
        var watermark = planned.Sequence;

        _ = await store.ApplyPlanAsync(new ApplyWorkPlanCommand(sessionId,
                           planned.Version,
                           Guid.NewGuid(),
                           AgentWorkSessionTaskOrigin.Agent,
                           [new WorkPlanTaskChange(firstTask, WorkPlanTaskOperation.Update, Status: AgentWorkSessionTaskStatus.Active)]));

        // The re-stamp is what makes ?sinceSeq= replay updates, not only inserts.
        var changed = await store.ListTasksAsync(sessionId, watermark);
        AssertEx.Equal(expected: 1, changed.Count);
        AssertEx.Equal(firstTask, changed[0].Id);
        AssertEx.Equal(AgentWorkSessionTaskStatus.Active, changed[0].Status);

        // ...and sinceSequence must only filter: the full page keeps its stable CreatedStep/Id order.
        var afterUpdate = await store.ListTasksAsync(sessionId);
        AssertEx.True(afterUpdate.Select(task => task.Id).SequenceEqual(displayOrder), "A re-stamped task must not jump position in the display list.");
    }

    [Test]
    public async Task ListEventsAsync_ReturnsExactlyTheTailAfterAWatermark()
    {
        using var fixture = new WorkSessionTestFixture();
        await using var context = await fixture.CreateSchemaAsync();
        var store = WorkSessionTestFixture.StoreFor(context);
        var sessionId = Guid.NewGuid();

        var created = await WorkSessionTestFixture.SeedAsync(store, sessionId);
        var first = await store.AppendEventAsync(new AppendWorkSessionEventCommand(sessionId, created.Version, "First"));
        var second = await store.AppendEventAsync(new AppendWorkSessionEventCommand(sessionId, first.Version, "Second"));
        var third = await store.AppendEventAsync(new AppendWorkSessionEventCommand(sessionId, second.Version, "Third"));

        var tail = await store.ListEventsAsync(sessionId, first.Sequence);
        AssertEx.True(tail.Select(entry => entry.EventType).SequenceEqual(["Second", "Third"]), "The tail must be exactly the events after the watermark.");
        AssertEx.Equal(third.Sequence, tail[^1].Sequence);
        AssertEx.Empty(await store.ListEventsAsync(sessionId, third.Sequence));
    }

    [Test]
    public async Task RepeatedOperationId_ReturnsTheFirstResultAndLeavesNothingTracked()
    {
        using var fixture = new WorkSessionTestFixture();
        await using var context = await fixture.CreateSchemaAsync();
        var store = WorkSessionTestFixture.StoreFor(context);
        var sessionId = Guid.NewGuid();
        var operationId = Guid.NewGuid();

        var created = await WorkSessionTestFixture.SeedAsync(store, sessionId);
        var first = await store.AppendFindingAsync(new AppendWorkSessionFindingCommand(sessionId,
                                   Guid.NewGuid(),
                                   created.Version,
                                   operationId,
                                   AgentWorkSessionFindingKind.Finding,
                                   "Recorded once."));

        // A replayed step re-derives the same operation id; the store must short-circuit it query-first.
        var replay = await store.AppendFindingAsync(new AppendWorkSessionFindingCommand(sessionId,
                                    Guid.NewGuid(),
                                    created.Version,
                                    operationId,
                                    AgentWorkSessionFindingKind.Finding,
                                    "Recorded twice."));

        AssertEx.Equal(first.Sequence, replay.Sequence);
        AssertEx.Equal(expected: 1, (await store.ListFindingsAsync(sessionId)).Count);
        AssertEx.Equal(expected: 1L, await fixture.RawCountAsync("agent_work_session_findings", "session_id", sessionId));

        // Insert-then-catch would leave the rejected row Added in the tracker and break the next write in this scope.
        AssertEx.Empty(context.ChangeTracker.Entries().Where(entry => entry.State == EntityState.Added));
        _ = await store.AppendEventAsync(new AppendWorkSessionEventCommand(sessionId, replay.Version, "StillUsable"));
    }

    [Test]
    public async Task ConcurrentWriters_AllocateDistinctSequencesFromSeparateScopes()
    {
        using var fixture = new WorkSessionTestFixture();
        var sessionId = Guid.NewGuid();

        await using var toolContext = await fixture.CreateSchemaAsync();
        await using var supervisorContext = fixture.CreateContext();
        var toolStore = WorkSessionTestFixture.StoreFor(toolContext);
        var supervisorStore = WorkSessionTestFixture.StoreFor(supervisorContext);

        var created = await WorkSessionTestFixture.SeedAsync(toolStore, sessionId);

        // A tool handler writes content with the version it read...
        var finding = await toolStore.AppendFindingAsync(new AppendWorkSessionFindingCommand(sessionId,
                                         Guid.NewGuid(),
                                         created.Version,
                                         Guid.NewGuid(),
                                         AgentWorkSessionFindingKind.Finding,
                                         "Written from the tool scope."));

        // ...while the supervisor moves the status from its own scope, holding a version that is already stale. The
        // sentinel is what keeps that legal: a status-only write has no lost update to protect against.
        var transitioned = await supervisorStore.TransitionStatusAsync(new TransitionWorkSessionStatusCommand(sessionId,
                                                    WorkSessionVersions.Any,
                                                    AgentWorkSessionStatus.Running));

        AssertEx.Equal(AgentWorkSessionStatus.Running, transitioned.Status);
        AssertEx.True(transitioned.Version > finding.Version, "Each committed writer must advance the session version.");

        var allocated = await AllocatedSequencesAsync(supervisorContext, sessionId);
        AssertEx.Equal(allocated.Count, allocated.Distinct().Count());
        AssertEx.True(allocated.SequenceEqual(Enumerable.Range(start: 1, allocated.Count).Select(value => (long)value)),
            $"Interleaved writers must leave no gap; got [{string.Join(", ", allocated)}].");
    }

    private static async Task<IReadOnlyList<long>> AllocatedSequencesAsync(NodeChatDbContext context, Guid sessionId)
    {
        var sequences = new List<long>();
        sequences.AddRange(await context.AgentWorkSessionEvents.AsNoTracking().Where(entity => entity.SessionId == sessionId).Select(entity => entity.Sequence).ToListAsync());
        sequences.AddRange(await context.AgentWorkSessionTasks.AsNoTracking().Where(entity => entity.SessionId == sessionId).Select(entity => entity.Sequence).ToListAsync());
        sequences.AddRange(await context.AgentWorkSessionFindings.AsNoTracking().Where(entity => entity.SessionId == sessionId).Select(entity => entity.Sequence).ToListAsync());
        sequences.AddRange(await context.AgentWorkSessionArtifacts.AsNoTracking().Where(entity => entity.SessionId == sessionId).Select(entity => entity.Sequence).ToListAsync());
        sequences.AddRange(await context.AgentWorkSessionCheckpoints.AsNoTracking().Where(entity => entity.SessionId == sessionId).Select(entity => entity.Sequence).ToListAsync());
        sequences.Sort();
        return sequences;
    }
}

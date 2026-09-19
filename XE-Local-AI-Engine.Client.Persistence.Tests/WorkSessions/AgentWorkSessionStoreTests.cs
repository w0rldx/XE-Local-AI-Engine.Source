namespace XE_Local_AI_Engine.Client.Persistence.Tests.WorkSessions;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

[Category(TestCategories.Integration)]
public sealed class AgentWorkSessionStoreTests
{
    [Test]
    public async Task CreateAsync_RoundTripsThroughAFreshContext()
    {
        using var fixture = new WorkSessionTestFixture();
        var sessionId = Guid.NewGuid();
        var objective = "OBJECTIVE-" + Guid.NewGuid().ToString("N");
        var command = WorkSessionTestFixture.CreateSeed(sessionId, "Research the KB", objective);

        await using (var writeContext = await fixture.CreateSchemaAsync())
        {
            var created = await WorkSessionTestFixture.StoreFor(writeContext).CreateAsync(command);
            AssertEx.Equal(AgentWorkSessionStatus.Draft, created.Status);
            AssertEx.Equal(expected: 1L, created.Version);
            AssertEx.Equal(expected: 0, created.StepCount);
            AssertEx.Equal(objective, created.Objective);
        }

        // A fresh context proves the materialization interceptor decrypted from disk, not from tracked plaintext.
        await using (var readContext = fixture.CreateContext())
        {
            var store = WorkSessionTestFixture.StoreFor(readContext);
            var session = await store.GetAsync(sessionId);
            AssertEx.Equal(objective, session.Objective);
            AssertEx.Equal("Research the KB", session.Title);
            AssertEx.Equal(AgentWorkSessionKind.Research, session.Kind);
            AssertEx.Equal(command.ConversationId, session.ConversationId);

            var byConversation = AssertEx.NotNull(await store.FindByConversationAsync(command.ConversationId));
            AssertEx.Equal(sessionId, byConversation.Id);
            AssertEx.Null(await store.FindByConversationAsync(Guid.NewGuid()));

            var events = await store.ListEventsAsync(sessionId);
            AssertEx.Equal(expected: 1, events.Count);
            AssertEx.Equal("SessionCreated", events[0].EventType);
        }
    }

    [Test]
    public async Task CreateAsync_RejectsTheReservedDevelopmentKind()
    {
        using var fixture = new WorkSessionTestFixture();
        await using var context = await fixture.CreateSchemaAsync();
        var store = WorkSessionTestFixture.StoreFor(context);

        _ = await AssertEx.ThrowsAsync<ArgumentException>(() =>
                              store.CreateAsync(WorkSessionTestFixture.CreateSeed(Guid.NewGuid(), kind: AgentWorkSessionKind.Development)));
    }

    [Test]
    public async Task UpdateAsync_ReplacesTitleAndObjectiveAndBumpsVersion()
    {
        using var fixture = new WorkSessionTestFixture();
        var sessionId = Guid.NewGuid();
        var replacement = "REPLACED-" + Guid.NewGuid().ToString("N");

        await using (var writeContext = await fixture.CreateSchemaAsync())
        {
            var store = WorkSessionTestFixture.StoreFor(writeContext);
            var created = await WorkSessionTestFixture.SeedAsync(store, sessionId);
            var updated = await store.UpdateAsync(new UpdateWorkSessionCommand { SessionId = sessionId, ExpectedVersion = created.Version, Title = "Renamed", Objective = replacement });
            AssertEx.Equal("Renamed", updated.Title);
            AssertEx.Equal(replacement, updated.Objective);
            AssertEx.Equal(created.Version + 1, updated.Version);
        }

        await using (var readContext = fixture.CreateContext())
        {
            var reread = await WorkSessionTestFixture.StoreFor(readContext).GetAsync(sessionId);
            AssertEx.Equal(replacement, reread.Objective);
            AssertEx.Equal("Renamed", reread.Title);
        }
    }

    [Test]
    public async Task ApplyPlanAsync_AppliesAddUpdateCompleteAndDropInOneBatch()
    {
        using var fixture = new WorkSessionTestFixture();
        var sessionId = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();

        await using (var writeContext = await fixture.CreateSchemaAsync())
        {
            var store = WorkSessionTestFixture.StoreFor(writeContext);
            var created = await WorkSessionTestFixture.SeedAsync(store, sessionId);
            var added = await store.ApplyPlanAsync(new ApplyWorkPlanCommand
            {
                SessionId = sessionId,
                ExpectedVersion = created.Version,
                OperationId = Guid.NewGuid(),
                Origin = AgentWorkSessionTaskOrigin.Agent,
                Changes = [
                                           new WorkPlanTaskChange { TaskId = first, Operation = WorkPlanTaskOperation.Add, Title = "Survey sources" },
                                           new WorkPlanTaskChange { TaskId = second, Operation = WorkPlanTaskOperation.Add, Title = "Draft findings" },
                                           new WorkPlanTaskChange { TaskId = third, Operation = WorkPlanTaskOperation.Add, Title = "Abandoned branch" }
                                       ]
            });

            _ = await store.ApplyPlanAsync(new ApplyWorkPlanCommand
            {
                SessionId = sessionId,
                ExpectedVersion = added.Version,
                OperationId = Guid.NewGuid(),
                Origin = AgentWorkSessionTaskOrigin.Agent,
                Changes = [
                                   new WorkPlanTaskChange { TaskId = first, Operation = WorkPlanTaskOperation.Complete },
                                   new WorkPlanTaskChange { TaskId = second, Operation = WorkPlanTaskOperation.Update, Title = "Draft the findings section", Status = AgentWorkSessionTaskStatus.Active },
                                   new WorkPlanTaskChange { TaskId = third, Operation = WorkPlanTaskOperation.Drop, BlockedReason = "Superseded by the second task." }
                               ]
            });
        }

        await using (var readContext = fixture.CreateContext())
        {
            var tasks = await WorkSessionTestFixture.StoreFor(readContext).ListTasksAsync(sessionId);
            AssertEx.Equal(expected: 3, tasks.Count);
            AssertEx.Equal(AgentWorkSessionTaskStatus.Done, tasks.Single(task => task.Id == first).Status);

            var updated = tasks.Single(task => task.Id == second);
            AssertEx.Equal(AgentWorkSessionTaskStatus.Active, updated.Status);
            AssertEx.Equal("Draft the findings section", updated.Title);

            var dropped = tasks.Single(task => task.Id == third);
            AssertEx.Equal(AgentWorkSessionTaskStatus.Dropped, dropped.Status);
            AssertEx.Equal("Superseded by the second task.", dropped.BlockedReason);
            AssertEx.Equal(AgentWorkSessionTaskOrigin.Agent, dropped.Origin);
        }
    }

    [Test]
    public async Task ApplyPlanAsync_RefusesATaskFromAnotherSessionAsParent()
    {
        using var fixture = new WorkSessionTestFixture();
        await using var context = await fixture.CreateSchemaAsync();
        var store = WorkSessionTestFixture.StoreFor(context);
        var sessionId = Guid.NewGuid();
        var created = await WorkSessionTestFixture.SeedAsync(store, sessionId);

        // The declared foreign keys never fire on this connection, so ownership is the store's to check.
        _ = await AssertEx.ThrowsAsync<WorkSessionNotFoundException>(() => store.ApplyPlanAsync(new ApplyWorkPlanCommand
        {
            SessionId = sessionId,
            ExpectedVersion = created.Version,
            OperationId = Guid.NewGuid(),
            Origin = AgentWorkSessionTaskOrigin.Agent,
            Changes = [new WorkPlanTaskChange { TaskId = Guid.NewGuid(), Operation = WorkPlanTaskOperation.Add, ParentTaskId = Guid.NewGuid(), Title = "Orphan child" }]
        }));
    }

    [Test]
    public async Task FindingsAndCheckpoints_RoundTripAndSupersedeTheirPredecessor()
    {
        using var fixture = new WorkSessionTestFixture();
        var sessionId = Guid.NewGuid();
        var firstFinding = Guid.NewGuid();
        var secondFinding = Guid.NewGuid();
        var checkpointId = Guid.NewGuid();
        var text = "FINDING-" + Guid.NewGuid().ToString("N");
        var state = "{\"currentTask\":\"none\"}";

        await using (var writeContext = await fixture.CreateSchemaAsync())
        {
            var store = WorkSessionTestFixture.StoreFor(writeContext);
            var created = await WorkSessionTestFixture.SeedAsync(store, sessionId);
            var withFirst = await store.AppendFindingAsync(new AppendWorkSessionFindingCommand
            {
                SessionId = sessionId,
                FindingId = firstFinding,
                ExpectedVersion = created.Version,
                OperationId = Guid.NewGuid(),
                Kind = AgentWorkSessionFindingKind.Finding,
                Text = text,
                SourceRef = "kb://doc/1"
            });
            var withSecond = await store.AppendFindingAsync(new AppendWorkSessionFindingCommand
            {
                SessionId = sessionId,
                FindingId = secondFinding,
                ExpectedVersion = withFirst.Version,
                OperationId = Guid.NewGuid(),
                Kind = AgentWorkSessionFindingKind.Decision,
                Text = "Chose the newer source.",
                SupersedesFindingId = firstFinding
            });
            _ = await store.AppendCheckpointAsync(new AppendWorkSessionCheckpointCommand
            {
                SessionId = sessionId,
                CheckpointId = checkpointId,
                ExpectedVersion = withSecond.Version,
                OperationId = Guid.NewGuid(),
                Step = 0,
                Summary = null,
                StateJson = state
            });
        }

        await using (var readContext = fixture.CreateContext())
        {
            var store = WorkSessionTestFixture.StoreFor(readContext);
            var findings = await store.ListFindingsAsync(sessionId);
            AssertEx.Equal(expected: 2, findings.Count);
            AssertEx.Equal(text, findings.Single(finding => finding.Id == firstFinding).Text);
            AssertEx.Equal("kb://doc/1", findings.Single(finding => finding.Id == firstFinding).SourceRef);
            AssertEx.True(findings.Single(finding => finding.Id == firstFinding).Superseded, "The first finding should be marked superseded.");
            AssertEx.False(findings.Single(finding => finding.Id == secondFinding).Superseded, "The replacement finding must not be superseded.");

            var checkpoint = AssertEx.NotNull(await store.GetLatestCheckpointAsync(sessionId));
            AssertEx.Equal(checkpointId, checkpoint.Id);
            AssertEx.Equal(state, checkpoint.StateJson);
            AssertEx.Null(checkpoint.Summary, "A checkpoint with no prose summary must round-trip as null, not as empty ciphertext.");
            AssertEx.Equal(checkpointId, (await store.GetAsync(sessionId)).LastCheckpointId);
        }
    }

    [Test]
    public async Task ListAsync_OrdersTheMostRecentlyTouchedSessionFirst()
    {
        using var fixture = new WorkSessionTestFixture();
        await using var context = await fixture.CreateSchemaAsync();
        var store = WorkSessionTestFixture.StoreFor(context);
        var older = Guid.NewGuid();
        var newer = Guid.NewGuid();

        var createdOlder = await WorkSessionTestFixture.SeedAsync(store, older, "Older");
        _ = await WorkSessionTestFixture.SeedAsync(store, newer, "Newer");

        // Same-millisecond creations are possible, so move the older row explicitly rather than trusting the clock.
        _ = await store.UpdateAsync(new UpdateWorkSessionCommand { SessionId = older, ExpectedVersion = createdOlder.Version, Title = "Older, touched" });

        var sessions = await store.ListAsync();
        AssertEx.Equal(expected: 2, sessions.Count);
        AssertEx.True(sessions[0].UpdatedAtUtc >= sessions[1].UpdatedAtUtc, "The list must order by the most recent update first.");
    }

    [Test]
    public async Task GetAsync_ThrowsForAnUnknownSession()
    {
        using var fixture = new WorkSessionTestFixture();
        await using var context = await fixture.CreateSchemaAsync();
        var store = WorkSessionTestFixture.StoreFor(context);

        _ = await AssertEx.ThrowsAsync<WorkSessionNotFoundException>(() => store.GetAsync(Guid.NewGuid()));
        _ = await AssertEx.ThrowsAsync<WorkSessionNotFoundException>(() => store.GetArtifactAsync(Guid.NewGuid()));
    }

    [Test]
    public async Task Feeds_ThrowForAnUnknownSessionRatherThanReturningAnEmptyCollection()
    {
        using var fixture = new WorkSessionTestFixture();
        await using var context = await fixture.CreateSchemaAsync();
        var store = WorkSessionTestFixture.StoreFor(context);
        var sessionId = Guid.NewGuid();

        _ = await AssertEx.ThrowsAsync<WorkSessionNotFoundException>(() => store.ListTasksAsync(sessionId));
        _ = await AssertEx.ThrowsAsync<WorkSessionNotFoundException>(() => store.ListFindingsAsync(sessionId));
        _ = await AssertEx.ThrowsAsync<WorkSessionNotFoundException>(() => store.ListArtifactsAsync(sessionId));
        _ = await AssertEx.ThrowsAsync<WorkSessionNotFoundException>(() => store.ListCheckpointsAsync(sessionId));
        _ = await AssertEx.ThrowsAsync<WorkSessionNotFoundException>(() => store.ListEventsAsync(sessionId));
        _ = await AssertEx.ThrowsAsync<WorkSessionNotFoundException>(() => store.GetLatestCheckpointAsync(sessionId));
    }
}

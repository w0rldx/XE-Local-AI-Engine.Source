namespace XE_Local_AI_Engine.Client.Persistence.Tests.DevWorkflows;

using System.Text;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

[Category(TestCategories.Integration)]
public sealed class DevWorkflowArtifactLineageTests
{
    /// <summary>
    ///     T-9: two appends from the same (run, node key, name) version one lineage rather than replacing — the exact
    ///     semantic the work-session artifact table does not have, which is why this is a separate table.
    /// </summary>
    [Test]
    public async Task AppendingTwiceFromOneNode_VersionsOneLineageInsteadOfReplacing()
    {
        using var fixture = new DevWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync();
        var store = DevWorkflowTestFixture.StoreFor(context);
        var seed = await DevWorkflowTestFixture.SeedRunAsync(store);

        var nodeRunId = Guid.NewGuid();
        var version = await DevWorkflowTestFixture.AddNodeRunAsync(store, seed.RunId, nodeRunId, "plan", seed.RunVersion);

        var firstId = Guid.NewGuid();
        var first = await AppendAsync(store, seed.RunId, firstId, nodeRunId, version, "plan", "hash-1");
        AssertEx.Null(first.SupersededArtifactId, "The first version of a lineage supersedes nothing.");

        var secondId = Guid.NewGuid();
        var second = await AppendAsync(store, seed.RunId, secondId, nodeRunId, first.Version, "plan", "hash-2");
        AssertEx.Equal(firstId, second.SupersededArtifactId, "A second append from the same node under the same name supersedes the first.");

        var artifacts = await store.ListArtifactsAsync(seed.RunId);
        AssertEx.Equal(expected: 2, artifacts.Count, "Versioning keeps both rows; nothing is replaced.");
        AssertEx.Equal(artifacts[0].LineageId, artifacts[1].LineageId, "Both versions belong to one lineage.");
        AssertEx.Equal(expected: 1, artifacts[0].Version);
        AssertEx.Equal(expected: 2, artifacts[1].Version);
        AssertEx.False(artifacts[0].IsLatest, "IsLatest is the max version per lineage, derived rather than stored.");
        AssertEx.True(artifacts[1].IsLatest);

        var fetched = await store.GetArtifactAsync(firstId);
        AssertEx.False(fetched.IsLatest, "A single-artifact read must reach the same answer as the list.");
    }

    /// <summary>
    ///     T-16: the regression guard. Materialized siblings share a template, so they emit artifacts under the same
    ///     logical name — and under a (run, name) lineage key sibling #2's patch would read back as version 2 of
    ///     sibling #1's, superseding work it has nothing to do with. Including the node key is what prevents that.
    /// </summary>
    [Test]
    public async Task MaterializedSiblingsUnderOneName_GetDistinctLineagesAndSupersedeNothing()
    {
        using var fixture = new DevWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync();
        var store = DevWorkflowTestFixture.StoreFor(context);
        var seed = await DevWorkflowTestFixture.SeedRunAsync(store);

        var firstSibling = Guid.NewGuid();
        var secondSibling = Guid.NewGuid();
        var materialized = await store.MaterializeNodeRunsAsync(new MaterializeDevWorkflowNodesCommand
        {
            RunId = seed.RunId,
            ExpectedVersion = seed.RunVersion,
            OperationId = Guid.NewGuid(),
            NodeRuns =
            [
                new DevWorkflowNodeRunSeed
                {
                    NodeRunId = firstSibling,
                    NodeKey = "implement#1",
                    NodeType = DevWorkflowNodeType.DevTask,
                    MaterializationIndex = 0
                },
                new DevWorkflowNodeRunSeed
                {
                    NodeRunId = secondSibling,
                    NodeKey = "implement#2",
                    NodeType = DevWorkflowNodeType.DevTask,
                    MaterializationIndex = 1
                }
            ]
        });

        var first = await AppendAsync(store, seed.RunId, Guid.NewGuid(), firstSibling, materialized.Version, "patch", "hash-a");
        var second = await AppendAsync(store, seed.RunId, Guid.NewGuid(), secondSibling, first.Version, "patch", "hash-b");

        AssertEx.Null(first.SupersededArtifactId);
        AssertEx.Null(second.SupersededArtifactId, "A sibling's artifact must never read as a new version of another sibling's work.");

        var artifacts = await store.ListArtifactsAsync(seed.RunId);
        AssertEx.Equal(expected: 2, artifacts.Count);
        AssertEx.False(artifacts[0].LineageId == artifacts[1].LineageId, "Parallel siblings own distinct lineages.");
        AssertEx.True(artifacts.All(artifact => artifact.Version == 1), "Both are the first version of their own lineage.");
        AssertEx.True(artifacts.All(artifact => artifact.IsLatest));
    }

    /// <summary>T-9, propagation half: marking flags exactly the consumers of the superseded version, and nothing else.</summary>
    [Test]
    public async Task MarkDependentsStale_FlagsOnlyWhatConsumedTheSupersededVersion()
    {
        using var fixture = new DevWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync();
        var store = DevWorkflowTestFixture.StoreFor(context);
        var seed = await DevWorkflowTestFixture.SeedRunAsync(store);

        var producerId = Guid.NewGuid();
        var consumerId = Guid.NewGuid();
        var bystanderId = Guid.NewGuid();
        var version = await store.MaterializeNodeRunsAsync(new MaterializeDevWorkflowNodesCommand
        {
            RunId = seed.RunId,
            ExpectedVersion = seed.RunVersion,
            OperationId = Guid.NewGuid(),
            NodeRuns =
            [
                new DevWorkflowNodeRunSeed
                {
                    NodeRunId = producerId,
                    NodeKey = "specify",
                    NodeType = DevWorkflowNodeType.Agent
                },
                new DevWorkflowNodeRunSeed
                {
                    NodeRunId = consumerId,
                    NodeKey = "plan",
                    NodeType = DevWorkflowNodeType.Agent
                },
                new DevWorkflowNodeRunSeed
                {
                    NodeRunId = bystanderId,
                    NodeKey = "research",
                    NodeType = DevWorkflowNodeType.Agent
                }
            ]
        });

        var specificationV1 = Guid.NewGuid();
        var appended = await AppendAsync(store, seed.RunId, specificationV1, producerId, version.Version, "specification", "spec-1");

        // The consumer records what it read, then produces its own artifact from it. The bystander produces one too,
        // having consumed nothing.
        var used = await store.RecordArtifactUsesAsync(new RecordDevWorkflowArtifactUsesCommand
        {
            RunId = seed.RunId,
            NodeRunId = consumerId,
            ExpectedVersion = appended.Version,
            OperationId = Guid.NewGuid(),
            ArtifactIds = [specificationV1]
        });
        var derived = await AppendAsync(store, seed.RunId, Guid.NewGuid(), consumerId, used.Version, "plan", "plan-1");
        var untouched = await AppendAsync(store, seed.RunId, Guid.NewGuid(), bystanderId, derived.Version, "notes", "notes-1");

        var specificationV2 = Guid.NewGuid();
        var superseding = await AppendAsync(store, seed.RunId, specificationV2, producerId, untouched.Version, "specification", "spec-2");
        AssertEx.Equal(specificationV1, superseding.SupersededArtifactId);

        _ = await store.MarkDependentsStaleAsync(new MarkDevWorkflowStaleCommand
        {
            RunId = seed.RunId,
            SupersededArtifactId = specificationV1,
            SupersedingArtifactId = specificationV2,
            ExpectedVersion = superseding.Version
        });

        var artifacts = await store.ListArtifactsAsync(seed.RunId);
        var plan = artifacts.Single(artifact => artifact.Name == "plan");
        AssertEx.True(plan.IsStale, "The plan was built from the superseded specification, so it is stale.");
        AssertEx.Equal(specificationV2, plan.StaleBecauseArtifactId, "Stale must name the version that caused it, not just say 'stale'.");
        AssertEx.Equal(DevWorkflowStaleReasons.SupersededInput, plan.StaleReason);
        AssertEx.True(plan.StaleSinceSequence is > 0);

        AssertEx.False(artifacts.Single(artifact => artifact.Name == "notes").IsStale, "A node that consumed nothing must not be flagged.");
        AssertEx.True(artifacts.Where(artifact => artifact.Name == "specification").All(artifact => !artifact.IsStale),
            "Marking is mark-only and one-directional; the specification itself is not stale.");

        var consumed = await store.ListConsumedArtifactIdsAsync(consumerId);
        AssertEx.Equal(expected: 1, consumed.Count);
        AssertEx.Equal(specificationV1, consumed[0], "The use points at the exact version consumed, which is what makes 'consumed v1, v2 exists' decidable.");
    }

    /// <summary>
    ///     And it must answer for an event written BEFORE the detail casing was fixed, because the log is append-only.
    ///     <para>
    ///         Every supersession recorded up to FX-D carries <c>{"SupersededArtifactId":…}</c>; a case-sensitive read
    ///         of the new spelling answers null for all of them, and a replay answering null skips a blob sweep it
    ///         still owes. The row is rewritten here to the old spelling because that is the only way to hold a
    ///         version of the past that the writer can no longer produce.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ReplayingAnAppendWrittenBeforeTheCasingFix_StillReturnsTheSupersededArtifactId()
    {
        using var fixture = new DevWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync();
        var store = DevWorkflowTestFixture.StoreFor(context);
        var seed = await DevWorkflowTestFixture.SeedRunAsync(store);

        var nodeRunId = Guid.NewGuid();
        var version = await DevWorkflowTestFixture.AddNodeRunAsync(store, seed.RunId, nodeRunId, "plan", seed.RunVersion);
        var firstId = Guid.NewGuid();
        var first = await AppendAsync(store, seed.RunId, firstId, nodeRunId, version, "plan", "hash-1");

        var secondId = Guid.NewGuid();
        var command = new AppendDevWorkflowArtifactCommand
        {
            RunId = seed.RunId,
            ArtifactId = secondId,
            NodeRunId = nodeRunId,
            ExpectedVersion = first.Version,
            OperationId = Guid.NewGuid(),
            Kind = DevWorkflowArtifactKind.Plan,
            Name = "plan",
            MediaType = "text/markdown",
            ContentSha256 = "hash-2",
            SizeBytes = 16,
            ManagedReference = $"{seed.RunId:N}/{secondId:N}"
        };
        _ = await store.AppendArtifactAsync(command);

        var recorded = context.DevWorkflowRunEvents.Single(entity => entity.EventType == DevWorkflowEventTypes.ArtifactSuperseded);
        recorded.DetailJson = Encoding.UTF8.GetBytes($$"""{"SupersededArtifactId":"{{firstId}}","SupersededManagedReference":"ref","Version":1}""");
        _ = await context.SaveChangesAsync();

        var replayed = await store.AppendArtifactAsync(command);

        AssertEx.Equal(firstId, replayed.SupersededArtifactId, "a row written in the old spelling must still answer, or the replay drops a sweep.");
    }

    /// <summary>
    ///     A replayed append must answer with the SAME superseded id, not a thinner result. The caller that owns the
    ///     blob store decides what to sweep from this field, so a replay reporting null would skip a sweep it still owes.
    /// </summary>
    [Test]
    public async Task ReplayingAnAppend_ReturnsTheRecordedSupersededArtifactId()
    {
        using var fixture = new DevWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync();
        var store = DevWorkflowTestFixture.StoreFor(context);
        var seed = await DevWorkflowTestFixture.SeedRunAsync(store);

        var nodeRunId = Guid.NewGuid();
        var version = await DevWorkflowTestFixture.AddNodeRunAsync(store, seed.RunId, nodeRunId, "plan", seed.RunVersion);

        var firstId = Guid.NewGuid();
        var first = await AppendAsync(store, seed.RunId, firstId, nodeRunId, version, "plan", "hash-1");

        var secondId = Guid.NewGuid();
        var command = new AppendDevWorkflowArtifactCommand
        {
            RunId = seed.RunId,
            ArtifactId = secondId,
            NodeRunId = nodeRunId,
            ExpectedVersion = first.Version,
            OperationId = Guid.NewGuid(),
            Kind = DevWorkflowArtifactKind.Plan,
            Name = "plan",
            MediaType = "text/markdown",
            ContentSha256 = "hash-2",
            SizeBytes = 16,
            ManagedReference = $"{seed.RunId:N}/{secondId:N}"
        };

        var written = await store.AppendArtifactAsync(command);
        AssertEx.Equal(firstId, written.SupersededArtifactId);

        var replayed = await store.AppendArtifactAsync(command);
        AssertEx.Equal(written.Sequence, replayed.Sequence, "A replay must answer with the watermark the first attempt allocated.");
        AssertEx.Equal(firstId, replayed.SupersededArtifactId, "A replay must return the recorded result, superseded id included.");
        AssertEx.Equal(expected: 2, (await store.ListArtifactsAsync(seed.RunId)).Count, "A replayed append must not insert a third row.");
    }

    /// <summary>
    ///     A node that re-runs and consumes its own previous output is a consumer of the very thing it replaces, so the
    ///     new version must not mark itself stale the moment it lands.
    /// </summary>
    [Test]
    public async Task MarkDependentsStale_DoesNotMarkTheSupersedingArtifactViaItsOwnUse()
    {
        using var fixture = new DevWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync();
        var store = DevWorkflowTestFixture.StoreFor(context);
        var seed = await DevWorkflowTestFixture.SeedRunAsync(store);

        var nodeRunId = Guid.NewGuid();
        var version = await DevWorkflowTestFixture.AddNodeRunAsync(store, seed.RunId, nodeRunId, "plan", seed.RunVersion);

        var planV1 = Guid.NewGuid();
        var first = await AppendAsync(store, seed.RunId, planV1, nodeRunId, version, "plan", "plan-1");

        // The re-attempt reads its own previous plan, then supersedes it.
        var used = await store.RecordArtifactUsesAsync(new RecordDevWorkflowArtifactUsesCommand
        {
            RunId = seed.RunId,
            NodeRunId = nodeRunId,
            ExpectedVersion = first.Version,
            OperationId = Guid.NewGuid(),
            ArtifactIds = [planV1]
        });
        var planV2 = Guid.NewGuid();
        var second = await AppendAsync(store, seed.RunId, planV2, nodeRunId, used.Version, "plan", "plan-2");
        AssertEx.Equal(planV1, second.SupersededArtifactId);

        _ = await store.MarkDependentsStaleAsync(new MarkDevWorkflowStaleCommand
        {
            RunId = seed.RunId,
            SupersededArtifactId = planV1,
            SupersedingArtifactId = planV2,
            ExpectedVersion = second.Version
        });

        var artifacts = await store.ListArtifactsAsync(seed.RunId);
        AssertEx.False(artifacts.Single(artifact => artifact.Id == planV2).IsStale, "The version that caused the supersession cannot be stale because of itself.");
    }

    /// <summary>An id from the wrong run must read as an error, not as the plausible zero an unconsumed artifact also reports.</summary>
    [Test]
    public async Task MarkDependentsStale_RejectsAnArtifactThatDoesNotBelongToTheRun()
    {
        using var fixture = new DevWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync();
        var store = DevWorkflowTestFixture.StoreFor(context);
        var seed = await DevWorkflowTestFixture.SeedRunAsync(store);

        _ = await AssertEx.ThrowsAsync<DevWorkflowNotFoundException>(() => store.MarkDependentsStaleAsync(new MarkDevWorkflowStaleCommand
            {
                RunId = seed.RunId,
                SupersededArtifactId = Guid.NewGuid(),
                SupersedingArtifactId = Guid.NewGuid(),
                ExpectedVersion = DevWorkflowVersions.Any
            }),
            "A superseded id that does not belong to the run must be rejected.");
    }

    /// <summary>Recording the same use twice must not duplicate the edge — the unique index is what staleness counts on.</summary>
    [Test]
    public async Task RecordArtifactUses_IsIdempotentPerNodeRunAndArtifact()
    {
        using var fixture = new DevWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync();
        var store = DevWorkflowTestFixture.StoreFor(context);
        var seed = await DevWorkflowTestFixture.SeedRunAsync(store);

        var producerId = Guid.NewGuid();
        var consumerId = Guid.NewGuid();
        var version = await store.MaterializeNodeRunsAsync(new MaterializeDevWorkflowNodesCommand
        {
            RunId = seed.RunId,
            ExpectedVersion = seed.RunVersion,
            OperationId = Guid.NewGuid(),
            NodeRuns =
            [
                new DevWorkflowNodeRunSeed
                {
                    NodeRunId = producerId,
                    NodeKey = "specify",
                    NodeType = DevWorkflowNodeType.Agent
                },
                new DevWorkflowNodeRunSeed
                {
                    NodeRunId = consumerId,
                    NodeKey = "plan",
                    NodeType = DevWorkflowNodeType.Agent
                }
            ]
        });

        var artifactId = Guid.NewGuid();
        var appended = await AppendAsync(store, seed.RunId, artifactId, producerId, version.Version, "specification", "spec-1");

        var first = await store.RecordArtifactUsesAsync(new RecordDevWorkflowArtifactUsesCommand
        {
            RunId = seed.RunId,
            NodeRunId = consumerId,
            ExpectedVersion = appended.Version,
            OperationId = Guid.NewGuid(),
            ArtifactIds = [artifactId]
        });

        // A distinct operation id, so this is a genuine second call rather than an idempotent replay.
        _ = await store.RecordArtifactUsesAsync(new RecordDevWorkflowArtifactUsesCommand
        {
            RunId = seed.RunId,
            NodeRunId = consumerId,
            ExpectedVersion = first.Version,
            OperationId = Guid.NewGuid(),
            ArtifactIds = [artifactId]
        });

        AssertEx.Equal(expected: 1L, await fixture.RawTableCountAsync("dev_workflow_artifact_uses"),
            "A repeated capture must not duplicate the consumed-by edge.");
    }

    private static Task<DevWorkflowMutationResult> AppendAsync(IDevWorkflowStore store,
        Guid runId,
        Guid artifactId,
        Guid nodeRunId,
        long expectedVersion,
        string name,
        string hash) =>
        store.AppendArtifactAsync(new AppendDevWorkflowArtifactCommand
        {
            RunId = runId,
            ArtifactId = artifactId,
            NodeRunId = nodeRunId,
            ExpectedVersion = expectedVersion,
            OperationId = Guid.NewGuid(),
            Kind = DevWorkflowArtifactKind.Plan,
            Name = name,
            MediaType = "text/markdown",
            ContentSha256 = hash,
            SizeBytes = 16,
            ManagedReference = $"{runId:N}/{artifactId:N}"
        });
}

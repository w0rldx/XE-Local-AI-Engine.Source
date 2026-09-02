namespace XE_Local_AI_Engine.Client.Persistence.Tests.DevWorkflows;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

public sealed class DevWorkflowPurgeCoverageTests
{
    /// <summary>
    ///     T-7: no <c>dev_workflow_*</c> table may declare <c>conversation_id</c> or <c>message_id</c>. A chat purge
    ///     deletes every table keyed by those columns, so one such column would put the whole workflow audit — the thing
    ///     the design exists to keep — inside the blast radius of deleting a conversation.
    /// </summary>
    [Test]
    public async Task NoWorkflowTable_IsKeyedByAConversationOrMessage()
    {
        using var fixture = new DevWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);

        var offenders = new List<string>();
        var inspected = 0;
        foreach (var entityType in context.Model.GetEntityTypes())
        {
            var tableName = entityType.GetTableName();
            if (tableName is null || !tableName.StartsWith("dev_workflow_", StringComparison.Ordinal))
            {
                continue;
            }

            inspected++;
            var storeObject = StoreObjectIdentifier.Table(tableName, entityType.GetSchema());
            if (entityType.GetProperties().Any(property => property.GetColumnName(storeObject) is "conversation_id" or "message_id"))
            {
                offenders.Add(tableName);
            }
        }

        AssertEx.Equal(expected: 9, inspected, "All nine dev_workflow_* tables must be discovered, or this assertion passes vacuously.");
        AssertEx.Empty(offenders,
            $"A dev_workflow_* table keyed by conversation_id/message_id would be deleted by the conversation footprint purge, "
            + $"destroying the workflow audit the design keeps separate on purpose: {string.Join(", ", offenders)}.");
    }

    /// <summary>
    ///     T-8: every work-item-scoped table must appear in <c>CoveredChildTables</c>. Cascades never fire on this
    ///     connection, so a table added to the model and forgotten here would orphan encrypted rows forever.
    /// </summary>
    [Test]
    public async Task CoveredChildTables_MatchesEveryWorkItemScopedTableInTheModel()
    {
        using var fixture = new DevWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);

        var discovered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entityType in context.Model.GetEntityTypes())
        {
            var tableName = entityType.GetTableName();
            if (tableName is null || !tableName.StartsWith("dev_workflow_", StringComparison.Ordinal))
            {
                continue;
            }

            // The root the purge deletes last, and the two tables that are deliberately NOT work-item-scoped: a
            // definition and a rule set each outlive every run that used them, which is why deleting a work item
            // leaves them standing.
            if (tableName is "dev_workflow_work_items" or "dev_workflow_definitions" or "dev_workflow_rule_sets")
            {
                continue;
            }

            discovered.Add(tableName);
        }

        AssertEx.Equal(expected: 6, discovered.Count, "Six work-item-scoped child tables must be discovered, or the assertions below pass vacuously.");

        var covered = new HashSet<string>(DevWorkflowPurge.CoveredChildTables, StringComparer.Ordinal);
        AssertEx.Empty(discovered.Except(covered, StringComparer.Ordinal),
            $"Work-item-scoped table(s) are not deleted by DevWorkflowPurge, so a delete would orphan their rows: "
            + $"{string.Join(", ", discovered.Except(covered, StringComparer.Ordinal))}.");
        AssertEx.Empty(covered.Except(discovered, StringComparer.Ordinal),
            $"DevWorkflowPurge.CoveredChildTables lists table(s) the model no longer scopes to a work item: "
            + $"{string.Join(", ", covered.Except(discovered, StringComparer.Ordinal))}.");
    }

    /// <summary>T-8, round trip: a full delete leaves zero rows across all seven work-item-scoped tables, and the definition survives.</summary>
    [Test]
    public async Task DeleteWorkItem_LeavesZeroRowsAcrossEverySevenTableAndKeepsTheDefinition()
    {
        using var fixture = new DevWorkflowTestFixture();
        Guid workItemId;

        await using (var context = await fixture.CreateSchemaAsync().ConfigureAwait(false))
        {
            var store = DevWorkflowTestFixture.StoreFor(context);
            var seed = await DevWorkflowTestFixture.SeedRunAsync(store).ConfigureAwait(false);
            workItemId = seed.WorkItemId;
            _ = await DevWorkflowTestFixture.CreateRuleSetAsync(store).ConfigureAwait(false);

            var producerId = Guid.NewGuid();
            var consumerId = Guid.NewGuid();
            var version = await DevWorkflowTestFixture.AddNodeRunAsync(store, seed.RunId, producerId, "research", seed.RunVersion).ConfigureAwait(false);
            version = await DevWorkflowTestFixture.AddNodeRunAsync(store, seed.RunId, consumerId, "approval", version, DevWorkflowNodeType.HumanGate).ConfigureAwait(false);

            var artifactId = Guid.NewGuid();
            var appended = await store.AppendArtifactAsync(new AppendDevWorkflowArtifactCommand(seed.RunId,
                                          artifactId,
                                          producerId,
                                          version,
                                          Guid.NewGuid(),
                                          DevWorkflowArtifactKind.Research,
                                          "brief",
                                          "text/markdown",
                                          "hash-1",
                                          SizeBytes: 10,
                                          "reference-1"))
                                      .ConfigureAwait(false);
            var used = await store.RecordArtifactUsesAsync(new RecordDevWorkflowArtifactUsesCommand(seed.RunId, consumerId, appended.Version, Guid.NewGuid(), [artifactId]))
                                  .ConfigureAwait(false);
            _ = await store.RecordDecisionAsync(new RecordDevWorkflowDecisionCommand(seed.RunId,
                               Guid.NewGuid(),
                               consumerId,
                               used.Version,
                               Guid.NewGuid(),
                               DevWorkflowDecisionKind.Approve))
                           .ConfigureAwait(false);

            _ = await AssertEx.ThrowsAsync<DevWorkflowRunInFlightException>(() => store.DeleteWorkItemAsync(workItemId),
                                  "A work item whose run is still live must not be deleted out from under the executor driving it.")
                              .ConfigureAwait(false);
            _ = await store.TransitionRunAsync(new TransitionDevWorkflowRunCommand(seed.RunId, DevWorkflowVersions.Any, DevWorkflowRunStatus.Cancelled))
                           .ConfigureAwait(false);

            var removed = await store.DeleteWorkItemAsync(workItemId).ConfigureAwait(false);
            AssertEx.True(removed.RemovedRows > 0, "The delete must report the rows it removed.");
            AssertEx.Equal(seed.RunId,
                removed.RunIds.Single(),
                "and the runs whose artifact bytes the caller still has to sweep — a run left out of this answer is bytes nothing points at any more.");
        }

        foreach (var table in new[]
                 {
                     "dev_workflow_work_items",
                     "dev_workflow_runs",
                     "dev_workflow_node_runs",
                     "dev_workflow_run_events",
                     "dev_workflow_decisions",
                     "dev_workflow_artifacts",
                     "dev_workflow_artifact_uses"
                 })
        {
            AssertEx.Equal(expected: 0L, await fixture.RawTableCountAsync(table).ConfigureAwait(false), $"{table} must be empty after the work item is deleted.");
        }

        AssertEx.Equal(expected: 1L, await fixture.RawTableCountAsync("dev_workflow_definitions").ConfigureAwait(false),
            "A definition is not work-item-scoped and survives by design.");
        AssertEx.Equal(expected: 1L, await fixture.RawTableCountAsync("dev_workflow_rule_sets").ConfigureAwait(false),
            "Neither is a rule set: deleting a work item must not take the policy documents every other run still resolves against.");
    }

    /// <summary>Deleting one run takes its subtree and leaves its work item — the per-run half of the same ordered delete.</summary>
    [Test]
    public async Task DeleteRun_TakesItsSubtreeAndLeavesTheWorkItemStanding()
    {
        using var fixture = new DevWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = DevWorkflowTestFixture.StoreFor(context);
        var seed = await DevWorkflowTestFixture.SeedRunAsync(store).ConfigureAwait(false);

        var nodeRunId = Guid.NewGuid();
        var version = await DevWorkflowTestFixture.AddNodeRunAsync(store, seed.RunId, nodeRunId, "research", seed.RunVersion).ConfigureAwait(false);
        _ = await store.AppendArtifactAsync(new AppendDevWorkflowArtifactCommand(seed.RunId,
                           Guid.NewGuid(),
                           nodeRunId,
                           version,
                           Guid.NewGuid(),
                           DevWorkflowArtifactKind.Research,
                           "brief",
                           "text/markdown",
                           "hash-1",
                           SizeBytes: 10,
                           "reference-1"))
                       .ConfigureAwait(false);

        await using (var transaction = await context.Database.BeginTransactionAsync().ConfigureAwait(false))
        {
            await DevWorkflowPurge.DeleteRunAsync(context, seed.RunId, CancellationToken.None).ConfigureAwait(false);
            await transaction.CommitAsync().ConfigureAwait(false);
        }

        context.ChangeTracker.Clear();

        foreach (var table in new[]
                 {
                     "dev_workflow_runs",
                     "dev_workflow_node_runs",
                     "dev_workflow_run_events",
                     "dev_workflow_artifacts"
                 })
        {
            AssertEx.Equal(expected: 0L, await fixture.RawTableCountAsync(table).ConfigureAwait(false), $"{table} must be empty after the run is deleted.");
        }

        AssertEx.Equal(expected: 1L, await fixture.RawTableCountAsync("dev_workflow_work_items").ConfigureAwait(false),
            "A work item outlives its runs; only deleting the work item takes it.");
    }
}

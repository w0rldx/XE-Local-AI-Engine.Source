namespace XE_Local_AI_Engine.Client.Persistence.Tests.GraphWorkflows;

using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

public sealed class GraphWorkflowPurgeCoverageTests
{
    /// <summary>
    ///     No <c>graph_workflow_*</c> table may declare <c>conversation_id</c> or <c>message_id</c>. A chat purge
    ///     deletes every table keyed by those columns, so one such column would put the whole workflow audit — the
    ///     thing the design exists to keep — inside the blast radius of deleting a conversation.
    /// </summary>
    [Test]
    public async Task NoWorkflowTable_IsKeyedByAConversationOrMessage()
    {
        using var fixture = new GraphWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);

        var offenders = new List<string>();
        var inspected = 0;
        foreach (var entityType in context.Model.GetEntityTypes())
        {
            var tableName = entityType.GetTableName();
            if (tableName is null || !tableName.StartsWith("graph_workflow_", StringComparison.Ordinal))
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

        AssertEx.Equal(expected: 4, inspected, "All four graph_workflow_* tables must be discovered, or this assertion passes vacuously.");
        AssertEx.Empty(offenders,
            "A graph_workflow_* table keyed by conversation_id/message_id would be deleted by the conversation footprint purge, "
            + $"destroying the workflow audit the design keeps separate on purpose: {string.Join(", ", offenders)}.");
    }

    /// <summary>
    ///     Every run-scoped table must appear in <c>CoveredChildTables</c>. Cascades never fire on this connection, so
    ///     a table added to the model and forgotten here would orphan encrypted rows forever.
    /// </summary>
    [Test]
    public async Task CoveredChildTables_MatchesEveryRunScopedTableInTheModel()
    {
        using var fixture = new GraphWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);

        var discovered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entityType in context.Model.GetEntityTypes())
        {
            var tableName = entityType.GetTableName();
            if (tableName is null || !tableName.StartsWith("graph_workflow_", StringComparison.Ordinal))
            {
                continue;
            }

            // The run root the purge deletes last, and the definition, which is deliberately NOT run-scoped: it
            // outlives every run that used it, which is why deleting a run leaves it standing.
            if (tableName is "graph_workflow_runs" or "graph_workflow_definitions")
            {
                continue;
            }

            _ = discovered.Add(tableName);
        }

        AssertEx.Equal(expected: 2, discovered.Count, "Two run-scoped child tables must be discovered, or the assertions below pass vacuously.");

        var covered = new HashSet<string>(GraphWorkflowPurge.CoveredChildTables, StringComparer.Ordinal);
        AssertEx.Empty(discovered.Except(covered, StringComparer.Ordinal),
            "Run-scoped table(s) are not deleted by GraphWorkflowPurge, so a delete would orphan their rows: "
            + $"{string.Join(", ", discovered.Except(covered, StringComparer.Ordinal))}.");
        AssertEx.Empty(covered.Except(discovered, StringComparer.Ordinal),
            "GraphWorkflowPurge.CoveredChildTables lists table(s) the model no longer scopes to a run: "
            + $"{string.Join(", ", covered.Except(discovered, StringComparer.Ordinal))}.");
    }

    /// <summary>
    ///     Deleting one run takes its subtree and leaves the definition — the other half of the same ordered delete.
    ///     <para>
    ///         A SECOND run of the same definition, with a subtree of its own, is what makes the counts an assertion
    ///         about scoping rather than about emptiness: every table below would read zero just as happily if the
    ///         delete were unscoped and took the lot.
    ///     </para>
    /// </summary>
    [Test]
    public async Task DeleteRun_TakesItsSubtreeAndLeavesTheDefinitionStanding()
    {
        using var fixture = new GraphWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = GraphWorkflowTestFixture.StoreFor(context);
        var definition = await GraphWorkflowTestFixture.SeedDefinitionAsync(store).ConfigureAwait(false);
        var runId = await GraphWorkflowTestFixture.SeedRunAsync(context, definition.Id).ConfigureAwait(false);
        _ = await GraphWorkflowTestFixture.SeedNodeRunAsync(context, runId, "analyze", GraphWorkflowNodeKind.Agent, inputJson: """{"run":{"input":1}}""")
                                          .ConfigureAwait(false);
        _ = await GraphWorkflowTestFixture.SeedRunEventAsync(context, runId, seq: 1, "run.started", """{"note":"seeded"}""").ConfigureAwait(false);

        var survivorRunId = await GraphWorkflowTestFixture.SeedRunAsync(context, definition.Id).ConfigureAwait(false);
        var survivorNodeRunId = await GraphWorkflowTestFixture
                                      .SeedNodeRunAsync(context, survivorRunId, "review", GraphWorkflowNodeKind.Agent, inputJson: """{"run":{"input":2}}""")
                                      .ConfigureAwait(false);
        var survivorEventId = await GraphWorkflowTestFixture.SeedRunEventAsync(context, survivorRunId, seq: 1, "run.started", """{"note":"survivor"}""")
                                                            .ConfigureAwait(false);

        await using (var transaction = await context.Database.BeginTransactionAsync().ConfigureAwait(false))
        {
            await GraphWorkflowPurge.DeleteRunAsync(context, runId, CancellationToken.None).ConfigureAwait(false);
            await transaction.CommitAsync().ConfigureAwait(false);
        }

        context.ChangeTracker.Clear();

        foreach (var table in new[]
                 {
                     "graph_workflow_runs",
                     "graph_workflow_node_runs",
                     "graph_workflow_run_events"
                 })
        {
            AssertEx.Equal(expected: 1L,
                await fixture.RawTableCountAsync(table).ConfigureAwait(false),
                $"{table} must hold exactly the other run's row after the first run is deleted.");
        }

        // By id rather than by count: one row of the right shape in the wrong run would satisfy the counts above.
        AssertEx.Equal(survivorRunId, await ScalarGuidAsync(fixture, "SELECT id FROM graph_workflow_runs;").ConfigureAwait(false), "The other run must be the survivor.");
        AssertEx.Equal(survivorNodeRunId,
            await ScalarGuidAsync(fixture, "SELECT id FROM graph_workflow_node_runs;").ConfigureAwait(false),
            "and its node run must be untouched.");
        AssertEx.Equal(survivorEventId, await ScalarGuidAsync(fixture, "SELECT id FROM graph_workflow_run_events;").ConfigureAwait(false), "and so must its event.");

        AssertEx.Equal(expected: 1L, await fixture.RawTableCountAsync("graph_workflow_definitions").ConfigureAwait(false),
            "A definition is not run-scoped and survives by design; only deleting the definition takes it.");
    }

    /// <summary>The one id a single-row query returns, read straight from the file rather than through the model.</summary>
    private static async Task<Guid> ScalarGuidAsync(GraphWorkflowTestFixture fixture, string sql)
    {
        var value = await fixture.RawScalarAsync(sql).ConfigureAwait(false);
        return value switch
        {
            Guid guid => guid,
            string text => Guid.Parse(text, CultureInfo.InvariantCulture),
            byte[] bytes => new Guid(bytes),
            _ => throw new InvalidOperationException($"'{sql}' returned no id this test can read.")
        };
    }
}

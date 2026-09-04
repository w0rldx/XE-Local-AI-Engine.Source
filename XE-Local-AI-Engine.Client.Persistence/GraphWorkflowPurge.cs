namespace XE_Local_AI_Engine.Client.Persistence;

using Microsoft.EntityFrameworkCore;

/// <summary>
///     Single source of truth for the complete DB footprint of one Graph Workflow run. The node-sqlite runtime
///     connection does not enable <c>PRAGMA foreign_keys=ON</c>, so <c>ON DELETE CASCADE</c> never fires and every
///     child table must be deleted explicitly or its rows orphan.
/// </summary>
/// <remarks>
///     Deletes DB rows only; the caller owns the enclosing transaction. Deleting rows that are already gone is a
///     harmless no-op, so the operation is idempotent. There is no definition-delete helper: a definition has no child
///     rows of its own — a run pins its own copy of the graph — so the store removes that row through the model.
/// </remarks>
public static class GraphWorkflowPurge
{
    /// <summary>
    ///     Every <c>graph_workflow_*</c> table below the run root that <see cref="DeleteRunAsync" /> deletes from,
    ///     excluding the root <c>graph_workflow_runs</c> itself. Exists so a test can enumerate every run-scoped table
    ///     in the EF model and assert it appears here — catching the drift this class's remarks warn about.
    ///     <c>graph_workflow_definitions</c> is deliberately absent: a definition is not run-scoped and outlives every
    ///     run that used it. Whenever a <c>DELETE FROM</c> statement below is added, removed, or changed, update this
    ///     list to match.
    /// </summary>
    internal static readonly IReadOnlyList<string> CoveredChildTables =
    [
        "graph_workflow_run_events",
        "graph_workflow_node_runs"
    ];

    /// <summary>
    ///     Deletes every child row and the run row for <paramref name="runId" />, in dependency order, on
    ///     <paramref name="dbContext" />'s connection. Runs within the caller's transaction.
    /// </summary>
    public static async Task DeleteRunAsync(NodeChatDbContext dbContext, Guid runId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM graph_workflow_run_events WHERE run_id = {0};", [runId], cancellationToken).ConfigureAwait(false);
        await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM graph_workflow_node_runs WHERE run_id = {0};", [runId], cancellationToken).ConfigureAwait(false);
        await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM graph_workflow_runs WHERE id = {0};", [runId], cancellationToken).ConfigureAwait(false);
    }
}

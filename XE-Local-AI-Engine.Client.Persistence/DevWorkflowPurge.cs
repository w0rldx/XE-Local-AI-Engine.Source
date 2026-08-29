namespace XE_Local_AI_Engine.Client.Persistence;

using Microsoft.EntityFrameworkCore;

/// <summary>
///     Single source of truth for the complete DB footprint of a dev-workflow work item and of one run. The node-sqlite
///     runtime connection does not enable <c>PRAGMA foreign_keys=ON</c>, so <c>ON DELETE CASCADE</c> never fires and
///     every child table must be deleted explicitly or its rows orphan.
/// </summary>
/// <remarks>
///     Deletes DB rows only; the caller owns the enclosing transaction, the on-disk artifact-blob teardown
///     (<c>IDevWorkflowArtifactBlobStore.DeleteRun</c> after the commit) and the work sessions the agent node-runs own —
///     deleting a work item does <em>not</em> cascade into <c>agent_work_sessions</c>, which has its own store and its
///     own ordered delete. Deleting rows that are already gone is a harmless no-op, so both operations are idempotent.
/// </remarks>
public static class DevWorkflowPurge
{
    /// <summary>
    ///     Every <c>dev_workflow_*</c> table below the work-item root that <see cref="DeleteWorkItemAsync" /> deletes
    ///     from, excluding the root <c>dev_workflow_work_items</c> itself. Exists so a test can enumerate every
    ///     work-item-scoped table in the EF model and assert it appears here — catching the drift this class's remarks
    ///     warn about. <c>dev_workflow_definitions</c> is deliberately absent: a definition is not work-item-scoped and
    ///     survives by design. Whenever a <c>DELETE FROM</c> statement below is added, removed, or changed, update this
    ///     list to match.
    /// </summary>
    internal static readonly IReadOnlyList<string> CoveredChildTables =
    [
        "dev_workflow_artifact_uses",
        "dev_workflow_artifacts",
        "dev_workflow_decisions",
        "dev_workflow_run_events",
        "dev_workflow_node_runs",
        "dev_workflow_runs"
    ];

    /// <summary>
    ///     Deletes every child row and the run row for <paramref name="runId" />, in dependency order, on
    ///     <paramref name="dbContext" />'s connection. Runs within the caller's transaction.
    /// </summary>
    public static async Task DeleteRunAsync(NodeChatDbContext dbContext, Guid runId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM dev_workflow_artifact_uses WHERE run_id = {0};", [runId], cancellationToken).ConfigureAwait(false);
        await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM dev_workflow_artifacts WHERE run_id = {0};", [runId], cancellationToken).ConfigureAwait(false);
        await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM dev_workflow_decisions WHERE run_id = {0};", [runId], cancellationToken).ConfigureAwait(false);
        await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM dev_workflow_run_events WHERE run_id = {0};", [runId], cancellationToken).ConfigureAwait(false);
        await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM dev_workflow_node_runs WHERE run_id = {0};", [runId], cancellationToken).ConfigureAwait(false);
        await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM dev_workflow_runs WHERE id = {0};", [runId], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Deletes every run below <paramref name="workItemId" />, their children, and the work-item row itself.
    /// </summary>
    public static async Task DeleteWorkItemAsync(NodeChatDbContext dbContext, Guid workItemId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        // Only dev_workflow_runs carries work_item_id, so the five per-run tables resolve through a subselect on it and
        // must go FIRST — once the run rows are gone the subselect no longer finds them.
        await dbContext.Database
                       .ExecuteSqlRawAsync("DELETE FROM dev_workflow_artifact_uses WHERE run_id IN (SELECT id FROM dev_workflow_runs WHERE work_item_id = {0});",
                           [workItemId],
                           cancellationToken)
                       .ConfigureAwait(false);
        await dbContext.Database
                       .ExecuteSqlRawAsync("DELETE FROM dev_workflow_artifacts WHERE run_id IN (SELECT id FROM dev_workflow_runs WHERE work_item_id = {0});",
                           [workItemId],
                           cancellationToken)
                       .ConfigureAwait(false);
        await dbContext.Database
                       .ExecuteSqlRawAsync("DELETE FROM dev_workflow_decisions WHERE run_id IN (SELECT id FROM dev_workflow_runs WHERE work_item_id = {0});",
                           [workItemId],
                           cancellationToken)
                       .ConfigureAwait(false);
        await dbContext.Database
                       .ExecuteSqlRawAsync("DELETE FROM dev_workflow_run_events WHERE run_id IN (SELECT id FROM dev_workflow_runs WHERE work_item_id = {0});",
                           [workItemId],
                           cancellationToken)
                       .ConfigureAwait(false);
        await dbContext.Database
                       .ExecuteSqlRawAsync("DELETE FROM dev_workflow_node_runs WHERE run_id IN (SELECT id FROM dev_workflow_runs WHERE work_item_id = {0});",
                           [workItemId],
                           cancellationToken)
                       .ConfigureAwait(false);

        await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM dev_workflow_runs WHERE work_item_id = {0};", [workItemId], cancellationToken).ConfigureAwait(false);
        await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM dev_workflow_work_items WHERE id = {0};", [workItemId], cancellationToken).ConfigureAwait(false);
    }
}

namespace XE_Local_AI_Engine.Client.Services.DevWorkflows;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     What one node-run attempt cost, assembled at the moment it settles from rows that are already PERSISTED.
/// </summary>
/// <remarks>
///     "Already persisted" is the whole constraint: a node run terminalizes on a dispatcher tick, in a different DI
///     scope from the work session that did the work and possibly in a different process, so nothing the run held in
///     memory is reachable here. Metadata only, under the trajectory policy: counts, ids, a served model name and
///     tool NAMES; no prompt, tool argument, tool result or transcript. See
///     docs/wiki/25-dev-workflows.md ("Node telemetry").
/// </remarks>
internal interface IDevWorkflowNodeTelemetrySource
{
    /// <summary>
    ///     The attempt's costs, or <see langword="null" /> when there is nothing to report — a structural node run with
    ///     neither a work session nor a development task, a purged session, or a status this is not collected for.
    /// </summary>
    Task<DevWorkflowNodeTelemetry?> CollectAsync(DevWorkflowNodeRunSnapshot nodeRun,
        DevWorkflowNodeRunStatus targetStatus,
        CancellationToken cancellationToken);
}

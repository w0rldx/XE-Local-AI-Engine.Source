namespace XE_Local_AI_Engine.Client.Services.DevWorkflows;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     What one node-run attempt cost, assembled at the moment it settles from rows that are already PERSISTED.
///     <para>
///         "Already persisted" is the whole constraint. A node run terminalizes on a dispatcher tick, in a different DI
///         scope from the work session that did the work and possibly in a different process after a restart — so
///         nothing the run held in memory is reachable here. The work session's provider-call budget in particular is
///         gone: its cap scope is disposed when the step that seeded it ends.
///     </para>
///     <para>
///         Metadata only, under the trajectory policy: counts, ids, a served model name and tool NAMES. No prompt, no
///         tool argument, no tool result, no transcript.
///     </para>
/// </summary>
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

namespace XE_Local_AI_Engine.Client.Services.Mcp.Server;

using System.ComponentModel;
using ModelContextProtocol.Server;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;

/// <summary>
///     Development-workflow tools of <see cref="NodeAdminMcpTools" />: listing runs and reading one run's detail,
///     both refused when development workflows are disabled on the node.
/// </summary>
public sealed partial class NodeAdminMcpTools
{
    [McpServerTool(Name = "list_workflow_runs")]
    [Description(
        "List development workflow runs as bounded lifecycle metadata — one row per work item's LATEST run, matching the operator's own list. Each row carries the run status, node tallies and pending-decision count. An optional case-insensitive run status filter may be supplied. Graphs, artifact contents, work-session transcripts and host paths are never returned, and nothing here starts, cancels or otherwise moves a run.")]
#pragma warning disable CA1707, IDE1006 // MCP's public JSON contract intentionally uses snake_case.
    public Task<McpWorkflowRunListResponse> ListWorkflowRunsAsync(CancellationToken cancellationToken,
        [Description("Maximum runs to return. Values are clamped to the server's configured bounded range.")]
        int? limit = null,
        [Description("Optional run status: pending, running, pausing, paused, waitingForApproval, cancelling, completed, failed, or cancelled.")]
        string? status = null)
#pragma warning restore CA1707, IDE1006
        =>
            InvokeAuditedAsync("list_workflow_runs", AuditArguments(("limit", limit), ("status", status)), async () =>
            {
                var boundedLimit = ClampWorkflowListLimit(limit);
                if (!_devWorkflowOptions.Enabled)
                {
                    return new McpWorkflowRunListResponse
                    {
                        Status = McpAdminToolFailureCodes.NotAvailable,
                        Runs = [],
                        Count = 0,
                        Limit = boundedLimit,
                        FailureCode = McpAdminToolFailureCodes.NotAvailable,
                        DisplayMessage = DevWorkflowsDisabledMessage
                    };
                }

                DevWorkflowRunStatus? parsedStatus = null;
                if (!string.IsNullOrWhiteSpace(status))
                {
                    var canonicalStatus = Enum.GetNames<DevWorkflowRunStatus>()
                                              .FirstOrDefault(name => string.Equals(name, status, StringComparison.OrdinalIgnoreCase));
                    if (canonicalStatus is null || !Enum.TryParse(canonicalStatus, out DevWorkflowRunStatus value))
                    {
                        return new McpWorkflowRunListResponse
                        {
                            Status = McpAdminToolFailureCodes.InvalidStatus,
                            Runs = [],
                            Count = 0,
                            Limit = boundedLimit,
                            FailureCode = McpAdminToolFailureCodes.InvalidStatus,
                            DisplayMessage = $"Cannot list: status must be one of {string.Join(", ", Enum.GetNames<DevWorkflowRunStatus>())}."
                        };
                    }

                    parsedStatus = value;
                }

                // The work-item list IS the run list here, so filtering needs no second query. Status and limit apply in memory, because the store
                // filters by a different enum; the ceiling is the node's work-item count — push the filter down only if a store-side run-status list appears.
                var workItems = await _devWorkflowStore.ListWorkItemsAsync(cancellationToken: cancellationToken);
                var runs = workItems.Where(item => item.LatestRunId is not null && item.LatestRunStatus is not null)
                                    .Where(item => parsedStatus is null || item.LatestRunStatus == parsedStatus)
                                    .Take(boundedLimit)
                                    .Select(static item => new McpWorkflowRunSummary(item.LatestRunId!.Value.ToString("D"),
                                        item.Id.ToString("D"),
                                        item.LatestRunDefinitionName,
                                        item.LatestRunStatus!.Value.ToString(),
                                        item.LatestRunNodes.Queued,
                                        item.LatestRunNodes.Running,
                                        item.LatestRunNodes.Completed,
                                        item.LatestRunNodes.Total,
                                        item.LatestRunNodes.PendingDecisionCount))
                                    .ToArray();
                return new McpWorkflowRunListResponse
                {
                    Status = "ok",
                    Runs = runs,
                    Count = runs.Length,
                    Limit = boundedLimit
                };
            }, static response => response.FailureCode is not null);

    [McpServerTool(Name = "get_workflow_run")]
    [Description(
        "Get one development workflow run by id: its status, node tallies, pending-decision count, failure class, sanitized terminal reason, start and end timestamps, and one bounded row per node run (key, type, status, attempt, max attempts). The pinned graph, artifact contents, work-session transcripts and host paths are never returned, and nothing here moves the run.")]
#pragma warning disable CA1707, IDE1006 // MCP's public JSON contract intentionally uses snake_case.
    public Task<McpWorkflowRunGetResponse> GetWorkflowRunAsync([Description("The canonical hyphenated UUID of the run, as returned by list_workflow_runs.")] string run_id,
        CancellationToken cancellationToken)
#pragma warning restore CA1707, IDE1006
        =>
            InvokeAuditedAsync("get_workflow_run", AuditArguments(("run_id", run_id)), async () =>
            {
                if (!_devWorkflowOptions.Enabled)
                {
                    return new McpWorkflowRunGetResponse
                    {
                        Status = McpAdminToolFailureCodes.NotAvailable,
                        Run = null,
                        FailureCode = McpAdminToolFailureCodes.NotAvailable,
                        DisplayMessage = DevWorkflowsDisabledMessage
                    };
                }

                if (!Guid.TryParseExact(run_id, "D", out var runId) || runId == Guid.Empty)
                {
                    return new McpWorkflowRunGetResponse
                    {
                        Status = McpAdminToolFailureCodes.InvalidRequest,
                        Run = null,
                        FailureCode = McpAdminToolFailureCodes.InvalidRequest,
                        DisplayMessage = "Cannot get: provide a valid run UUID."
                    };
                }

                DevWorkflowRunDetail detail;
                try
                {
                    detail = await _devWorkflowRunService.GetAsync(runId, cancellationToken);
                }
                catch (DevWorkflowNotFoundException)
                {
                    return new McpWorkflowRunGetResponse
                    {
                        Status = "not_found",
                        Run = null,
                        FailureCode = McpAdminToolFailureCodes.RunNotFound,
                        DisplayMessage = "Run not found."
                    };
                }

                // Names only, over the summary projection that never decrypts a graph blob — the same read the run
                // detail endpoint uses to label a run.
                var definitions = await _devWorkflowStore.ListDefinitionsAsync(includeArchived: true, cancellationToken);
                var run = detail.Run;
                return new McpWorkflowRunGetResponse
                {
                    Status = "ok",
                    Run = new McpWorkflowRunDetail(run.Id.ToString("D"),
                        run.WorkItemId.ToString("D"),
                        definitions.FirstOrDefault(definition => definition.Id == run.DefinitionId)?.Name,
                        run.Status.ToString(),
                        detail.NodeRuns.Count(static nodeRun => nodeRun.Status == DevWorkflowNodeRunStatus.Queued),
                        detail.NodeRuns.Count(static nodeRun => nodeRun.Status == DevWorkflowNodeRunStatus.Running),
                        detail.NodeRuns.Count(static nodeRun => nodeRun.Status == DevWorkflowNodeRunStatus.Succeeded),
                        detail.NodeRuns.Count,
                        detail.PendingDecisionCount,
                        run.FailureClass,
                        run.TerminalReason,
                        run.StartedAtUtc,
                        run.EndedAtUtc,
                        [
                            .. detail.NodeRuns.Select(static nodeRun => new McpWorkflowNodeRunSummary
                            {
                                NodeKey = nodeRun.NodeKey,
                                NodeType = nodeRun.NodeType.ToString(),
                                Status = nodeRun.Status.ToString(),
                                Attempt = nodeRun.Attempt,
                                MaxAttempts = nodeRun.MaxAttempts
                            })
                        ])
                };
            }, static response => response.FailureCode is not null);

    private int ClampWorkflowListLimit(int? limit) =>
        Math.Clamp(limit ?? _devWorkflowOptions.McpDefaultListLimit, 1, _devWorkflowOptions.McpMaxListLimit);
}

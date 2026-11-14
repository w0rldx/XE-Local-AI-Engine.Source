namespace XE_Local_AI_Engine.Client.Services.Mcp.Server;

using System.ComponentModel;
using ModelContextProtocol.Server;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Graph workflow tools of <see cref="NodeAdminMcpTools" />: listing runs and reading one run's detail, both refused
///     when Graph Workflows is disabled on the node.
/// </summary>
public sealed partial class NodeAdminMcpTools
{
    private const int DefaultWorkflowListLimit = 20;
    private const int MaxWorkflowListLimit = 50;

    [McpServerTool(Name = "list_workflow_runs", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "List Graph workflow runs, newest first, as bounded lifecycle metadata: run id, definition, status, failure class, trigger source and timestamps. An optional case-insensitive run status filter may be supplied. Graphs, node inputs and outputs, transcripts and host paths are never returned, and nothing here starts, cancels or otherwise moves a run.")]
    // Tool parameter names are snake_case: they are MCP's public JSON contract.
    public Task<McpWorkflowRunListResponse> ListWorkflowRunsAsync(CancellationToken cancellationToken,
        [Description("Maximum runs to return. Values are clamped to the server's bounded range.")] int? limit = null,
        [Description("Optional run status: pending, running, waitingForApproval, cancelling, completed, failed, or cancelled.")]
        string? status = null) =>
        InvokeAuditedAsync("list_workflow_runs", AuditArguments(new("limit", limit), new("status", status)), async () =>
        {
            var boundedLimit = Math.Clamp(limit ?? DefaultWorkflowListLimit, 1, MaxWorkflowListLimit);
            if (!await _runtimeSettings.GetGraphWorkflowsEnabledAsync(cancellationToken))
            {
                return new McpWorkflowRunListResponse
                {
                    Status = McpAdminToolFailureCodes.NotAvailable,
                    Runs = [],
                    Count = 0,
                    Limit = boundedLimit,
                    FailureCode = McpAdminToolFailureCodes.NotAvailable,
                    DisplayMessage = GraphWorkflowsDisabledMessage
                };
            }

            GraphWorkflowRunStatus? parsedStatus = null;
            if (!string.IsNullOrWhiteSpace(status))
            {
                // Matched against the names: Enum.TryParse would also take "99".
                var canonicalStatus = Enum.GetNames<GraphWorkflowRunStatus>()
                                          .FirstOrDefault(name => string.Equals(name, status, StringComparison.OrdinalIgnoreCase));
                if (canonicalStatus is null)
                {
                    return new McpWorkflowRunListResponse
                    {
                        Status = McpAdminToolFailureCodes.InvalidStatus,
                        Runs = [],
                        Count = 0,
                        Limit = boundedLimit,
                        FailureCode = McpAdminToolFailureCodes.InvalidStatus,
                        DisplayMessage = $"Cannot list: status must be one of {string.Join(", ", Enum.GetNames<GraphWorkflowRunStatus>())}."
                    };
                }

                parsedStatus = Enum.Parse<GraphWorkflowRunStatus>(canonicalStatus);
            }

            var runs = await _graphWorkflowStore.ListRunsAsync(parsedStatus, boundedLimit, cancellationToken);
            var names = await DefinitionNamesAsync(cancellationToken);
            McpWorkflowRunSummary[] rows = [.. runs.Select(run => Summary(run, names))];
            return new McpWorkflowRunListResponse
            {
                Status = "ok",
                Runs = rows,
                Count = rows.Length,
                Limit = boundedLimit
            };
        }, static response => response.FailureCode is not null);

    [McpServerTool(Name = "get_workflow_run", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Get one Graph workflow run by id: the list row's fields, node tallies, pending-decision count, and one bounded row per node run (key, kind, status, attempt). The pinned graph, node inputs and outputs, transcripts and host paths are never returned, and nothing here moves the run.")]
    public Task<McpWorkflowRunGetResponse> GetWorkflowRunAsync([Description("The canonical hyphenated UUID of the run, as returned by list_workflow_runs.")] string run_id,
        CancellationToken cancellationToken) =>
        InvokeAuditedAsync("get_workflow_run", AuditArguments(new KeyValuePair<string, object?>("run_id", run_id)), async () =>
        {
            if (!await _runtimeSettings.GetGraphWorkflowsEnabledAsync(cancellationToken))
            {
                return new McpWorkflowRunGetResponse
                {
                    Status = McpAdminToolFailureCodes.NotAvailable,
                    Run = null,
                    FailureCode = McpAdminToolFailureCodes.NotAvailable,
                    DisplayMessage = GraphWorkflowsDisabledMessage
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

            GraphWorkflowRunSnapshot run;
            try
            {
                run = await _graphWorkflowStore.GetRunAsync(runId, cancellationToken);
            }
            catch (GraphWorkflowNotFoundException)
            {
                return new McpWorkflowRunGetResponse
                {
                    Status = "not_found",
                    Run = null,
                    FailureCode = McpAdminToolFailureCodes.RunNotFound,
                    DisplayMessage = "Run not found."
                };
            }

            var nodeRuns = await _graphWorkflowStore.ListNodeRunsAsync(runId, cancellationToken);
            var summary = Summary(run, await DefinitionNamesAsync(cancellationToken));
            return new McpWorkflowRunGetResponse
            {
                Status = "ok",
                Run = new McpWorkflowRunDetail(summary.RunId,
                    summary.DefinitionId,
                    summary.DefinitionName,
                    summary.Status,
                    summary.FailureClass,
                    summary.TriggerSource,
                    summary.CreatedAtUtc,
                    summary.StartedAtUtc,
                    summary.CompletedAtUtc,
                    nodeRuns.Count(static nodeRun => nodeRun.Status is GraphWorkflowNodeRunStatus.Pending or GraphWorkflowNodeRunStatus.Queued),
                    nodeRuns.Count(static nodeRun => nodeRun.Status == GraphWorkflowNodeRunStatus.Running),
                    nodeRuns.Count(static nodeRun => nodeRun.Status == GraphWorkflowNodeRunStatus.Succeeded),
                    nodeRuns.Count,
                    nodeRuns.Count(static nodeRun => nodeRun.Status == GraphWorkflowNodeRunStatus.WaitingForApproval),
                    [
                        .. nodeRuns.Select(static nodeRun => new McpWorkflowNodeRunSummary
                        {
                            NodeKey = nodeRun.NodeKey,
                            NodeType = nodeRun.Kind.ToString(),
                            Status = nodeRun.Status.ToString(),
                            Attempt = nodeRun.Attempt
                        })
                    ])
            };
        }, static response => response.FailureCode is not null);

    /// <summary>Definition names over the summary projection, which never decrypts a graph blob.</summary>
    private async Task<IReadOnlyDictionary<Guid, string>> DefinitionNamesAsync(CancellationToken cancellationToken) =>
        (await _graphWorkflowStore.ListDefinitionsAsync(cancellationToken)).ToDictionary(static definition => definition.Id, static definition => definition.Name);

    private static McpWorkflowRunSummary Summary(GraphWorkflowRunSummary run,IReadOnlyDictionary<Guid, string> names) =>
        new(run.Id.ToString("D"),
            run.DefinitionId.ToString("D"),
            names.GetValueOrDefault(run.DefinitionId),
            run.Status.ToString(),
            run.FailureClass == GraphWorkflowFailureClass.None ? null : run.FailureClass.ToString(),
            run.TriggerSource.ToString(),
            run.CreatedAtUtc,
            run.StartedAtUtc,
            run.CompletedAtUtc);
}

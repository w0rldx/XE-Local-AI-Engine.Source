namespace XE_Local_AI_Engine.Client.Hubs;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1;
using XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1.Mappers;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;

public static class GraphWorkflowHubEvents
{
    public const string Changed = "graphWorkflowChanged";
}

/// <summary>
///     What changed and where the run now stands. <see cref="Kind" /> is lowercase on the wire — the client switches
///     on the literal — and the payload deliberately carries no content: the subscriber re-reads the named feed from
///     its own watermark, so a dropped push degrades to a late read rather than to a wrong render.
/// </summary>
public sealed class GraphWorkflowChanged
{
    public required Guid RunId { get; init; }

    public required long Seq { get; init; }

    public required string Kind { get; init; }
}

public sealed class GraphWorkflowRunSubscriptionSnapshot
{
    public required Guid RunId { get; init; }

    public required string Status { get; init; }

    public required int QueuedNodeCount { get; init; }

    public required int RunningNodeCount { get; init; }

    public required int PendingDecisionCount { get; init; }

    public required long LastSeq { get; init; }

    public required IReadOnlyList<GraphWorkflowRunEventResponse> Events { get; init; }

    public required bool ReplayTruncated { get; init; }
}

/// <summary>
///     Operator-only live notifications for one graph workflow run.
///     <para>
///         Modelled on <see cref="DevWorkflowRunHub" /> and explicitly NOT on a per-run subscription hub: there is no
///         in-memory buffer, because run events are persisted append-only with a monotonic sequence and the persisted
///         log IS the replay authority. Nor does a disconnect cancel anything — a workflow run is durable and outlives both
///         the browser tab and the engine, which is the property this module exists to prove.
///     </para>
/// </summary>
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = NodeAuthorizationPolicies.Operator)]
public sealed class GraphWorkflowRunHub : Hub
{
    private readonly GraphWorkflowOptions _options;
    private readonly IGraphWorkflowRunService _runs;

    public GraphWorkflowRunHub(IGraphWorkflowRunService runs, IOptions<GraphWorkflowOptions> options)
    {
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        ArgumentNullException.ThrowIfNull(runs);
        _runs = runs;
    }

    public async Task<GraphWorkflowRunSubscriptionSnapshot> SubscribeRun(Guid runId, long afterSeq)
    {
        if (!_options.Enabled)
        {
            throw new HubException("Graph workflows are disabled on this node.");
        }

        if (runId == Guid.Empty)
        {
            throw new HubException("Graph workflow run id is required.");
        }

        if (afterSeq < 0)
        {
            throw new HubException("Graph workflow replay sequence is invalid.");
        }

        var cancellationToken = Context.ConnectionAborted;
        GraphWorkflowRunDetail detail;
        try
        {
            detail = await _runs.GetRunAsync(runId, cancellationToken);
        }
        catch (GraphWorkflowNotFoundException)
        {
            throw new HubException("Graph workflow run was not found.");
        }

        // Join BEFORE reading the replay: the other order leaves a window in which a change published between the read
        // and the join reaches nobody. The overlap this creates is harmless — every push is an idempotent notification
        // keyed by sequence.
        await Groups.AddToGroupAsync(Context.ConnectionId, GraphWorkflowHubGroups.Run(runId), cancellationToken);

        // The same paged read the event endpoint answers with, capped at the same configured window and carrying the
        // same watermark: a client can move between a subscription and the feed without a gap or a repeat, because
        // neither side owns a copy of that arithmetic. The watermark is the last row actually HANDED over — never the
        // run's own sequence, which on a truncated page is past events this snapshot did not carry and nothing ever
        // replays again.
        var replay = await _runs.ListEventsAsync(runId, afterSeq, cancellationToken);
        return new GraphWorkflowRunSubscriptionSnapshot
        {
            RunId = runId,
            Status = detail.Run.Status.ToString(),
            QueuedNodeCount = detail.NodeRuns.Count(static nodeRun => nodeRun.Status == GraphWorkflowNodeRunStatus.Queued),
            RunningNodeCount = detail.NodeRuns.Count(static nodeRun => nodeRun.Status == GraphWorkflowNodeRunStatus.Running),
            PendingDecisionCount = detail.NodeRuns.Count(static nodeRun => nodeRun.Status == GraphWorkflowNodeRunStatus.WaitingForApproval),
            LastSeq = replay.LastSeq,
            Events = [.. replay.Events.Select(static @event => @event.ToResponse())],
            ReplayTruncated = replay.ReplayTruncated
        };
    }

    public Task UnsubscribeRun(Guid runId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, GraphWorkflowHubGroups.Run(runId), Context.ConnectionAborted);
}

internal static class GraphWorkflowHubGroups
{
    public static string Run(Guid runId) =>
        string.Concat("graph-workflow-run-", runId.ToString("N"));
}

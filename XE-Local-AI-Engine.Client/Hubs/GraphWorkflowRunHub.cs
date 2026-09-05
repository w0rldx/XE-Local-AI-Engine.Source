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
public sealed record GraphWorkflowChanged(Guid RunId, long Seq, string Kind);

public sealed record GraphWorkflowRunSubscriptionSnapshot(
    Guid RunId,
    string Status,
    int QueuedNodeCount,
    int RunningNodeCount,
    int PendingDecisionCount,
    long LastSeq,
    IReadOnlyList<GraphWorkflowRunEventResponse> Events,
    bool ReplayTruncated);

/// <summary>
///     Operator-only live notifications for one graph workflow run.
///     <para>
///         Modelled on <see cref="DevWorkflowRunHub" /> and explicitly NOT on <c>PreviewWorkflowHub</c>: there is no
///         in-memory buffer, because run events are persisted append-only with a monotonic sequence and the store IS
///         the replay authority. Nor does a disconnect cancel anything — a workflow run is durable and outlives both
///         the browser tab and the engine, which is the property this module exists to prove.
///     </para>
/// </summary>
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = NodeAuthorizationPolicies.Operator)]
public sealed class GraphWorkflowRunHub(IGraphWorkflowStore store, IGraphWorkflowRunService runs, IOptions<GraphWorkflowOptions> options) : Hub
{
    private readonly GraphWorkflowOptions _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
    private readonly IGraphWorkflowRunService _runs = runs ?? throw new ArgumentNullException(nameof(runs));
    private readonly IGraphWorkflowStore _store = store ?? throw new ArgumentNullException(nameof(store));

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
            detail = await _runs.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
        }
        catch (GraphWorkflowNotFoundException)
        {
            throw new HubException("Graph workflow run was not found.");
        }

        // Join BEFORE reading the replay: the other order leaves a window in which a change published between the read
        // and the join reaches nobody. The overlap this creates is harmless — every push is an idempotent notification
        // keyed by sequence.
        await Groups.AddToGroupAsync(Context.ConnectionId, GraphWorkflowHubGroups.Run(runId), cancellationToken).ConfigureAwait(false);

        // One over the configured replay window, so "there is more" is observed rather than inferred from a full page.
        // The window is the OPTION the event endpoint pages by, not a second constant that could drift from it.
        var replayLimit = _options.EventReplayLimit;
        var events = await _store.ListEventsAsync(runId, afterSeq, replayLimit + 1, cancellationToken).ConfigureAwait(false);
        var replayed = events.Take(replayLimit).ToList();

        // The watermark the subscriber may resume from is the highest row it has actually SEEN. The run's own sequence
        // was read before the group join and before this page, so a change committed in between would leave a client
        // resuming past events this snapshot never carried.
        var lastSeq = Math.Max(detail.Run.Seq, replayed.Count == 0 ? 0 : replayed[^1].Seq);
        return new GraphWorkflowRunSubscriptionSnapshot(runId,
            detail.Run.Status.ToString(),
            detail.NodeRuns.Count(static nodeRun => nodeRun.Status == GraphWorkflowNodeRunStatus.Queued),
            detail.NodeRuns.Count(static nodeRun => nodeRun.Status == GraphWorkflowNodeRunStatus.Running),
            detail.NodeRuns.Count(static nodeRun => nodeRun.Status == GraphWorkflowNodeRunStatus.WaitingForApproval),
            lastSeq,
            [.. replayed.Select(static @event => @event.ToResponse())],
            events.Count > replayLimit);
    }

    public Task UnsubscribeRun(Guid runId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, GraphWorkflowHubGroups.Run(runId));
}

internal static class GraphWorkflowHubGroups
{
    public static string Run(Guid runId) =>
        string.Concat("graph-workflow-run-", runId.ToString("N"));
}

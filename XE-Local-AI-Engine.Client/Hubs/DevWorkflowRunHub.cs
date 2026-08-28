namespace XE_Local_AI_Engine.Client.Hubs;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1;
using XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1.Mappers;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;

public static class DevWorkflowHubEvents
{
    public const string Changed = "devWorkflowChanged";
}

/// <summary>
///     What changed and where the run now stands. <see cref="Kind" /> is lowercase on the wire — the client switches
///     on the literal — and the payload deliberately carries no content: the subscriber re-reads the named feed from
///     its own watermark, so a dropped push degrades to a late read rather than to a wrong render.
/// </summary>
public sealed record DevWorkflowChanged(Guid RunId, long Seq, string Kind);

public sealed record DevWorkflowRunSubscriptionSnapshot(
    Guid RunId,
    string Status,
    int QueuedNodeCount,
    int RunningNodeCount,
    int PendingDecisionCount,
    Guid? BlockingGateNodeRunId,
    long LastSeq,
    IReadOnlyList<DevWorkflowRunEventResponse> Events,
    bool ReplayTruncated);

/// <summary>
///     Operator-only live notifications for one development workflow run.
///     <para>
///         Modelled on <see cref="WorkSessionHub" /> and explicitly NOT on <c>PreviewWorkflowHub</c>: there is no
///         in-memory buffer, because run events are persisted append-only with a monotonic sequence and the store IS
///         the replay authority. Nor does a disconnect cancel anything — a workflow run is durable and outlives both
///         the browser tab and the engine, which is the property this module exists to prove.
///     </para>
/// </summary>
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = NodeAuthorizationPolicies.Operator)]
public sealed class DevWorkflowRunHub(IDevWorkflowStore store, IDevWorkflowRunService runs, IOptions<DevWorkflowOptions> options) : Hub
{
    /// <summary>
    ///     How many persisted events one subscribe hands back. Past this the snapshot says so and the client pages the
    ///     event feed by <c>sinceSeq</c> — one extra round trip on a long-lived run, against an unbounded first frame
    ///     for every subscriber.
    /// </summary>
    private const int ReplayCap = 200;

    private readonly DevWorkflowOptions _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
    private readonly IDevWorkflowRunService _runs = runs ?? throw new ArgumentNullException(nameof(runs));
    private readonly IDevWorkflowStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public async Task<DevWorkflowRunSubscriptionSnapshot> SubscribeRun(Guid runId, long afterSeq)
    {
        if (!_options.Enabled)
        {
            throw new HubException("Development workflows are disabled on this node.");
        }

        if (runId == Guid.Empty)
        {
            throw new HubException("Development workflow run id is required.");
        }

        if (afterSeq < 0)
        {
            throw new HubException("Development workflow replay sequence is invalid.");
        }

        var cancellationToken = Context.ConnectionAborted;
        DevWorkflowRunDetail detail;
        try
        {
            detail = await _runs.GetAsync(runId, cancellationToken).ConfigureAwait(false);
        }
        catch (DevWorkflowNotFoundException)
        {
            throw new HubException("Development workflow run was not found.");
        }

        // Join BEFORE reading the replay: the other order leaves a window in which a change published between the read
        // and the join reaches nobody. The overlap this creates is harmless — every push is an idempotent notification
        // keyed by sequence.
        await Groups.AddToGroupAsync(Context.ConnectionId, DevWorkflowHubGroups.Run(runId), cancellationToken).ConfigureAwait(false);

        // One over the cap, so "there is more" is observed rather than inferred from a full page.
        var events = await _store.ListEventsAsync(runId, afterSeq, ReplayCap + 1, cancellationToken).ConfigureAwait(false);
        return new DevWorkflowRunSubscriptionSnapshot(runId,
            detail.Run.Status.ToString(),
            detail.NodeRuns.Count(static nodeRun => nodeRun.Status == Persistence.Entities.DevWorkflowNodeRunStatus.Queued),
            detail.NodeRuns.Count(static nodeRun => nodeRun.Status == Persistence.Entities.DevWorkflowNodeRunStatus.Running),
            detail.PendingDecisionCount,
            detail.BlockingGateNodeRunId,
            detail.Run.LastSequence,
            [.. events.Take(ReplayCap).Select(DevWorkflowContractMapper.ToResponse)],
            events.Count > ReplayCap);
    }

    public Task UnsubscribeRun(Guid runId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, DevWorkflowHubGroups.Run(runId));
}

internal static class DevWorkflowHubGroups
{
    public static string Run(Guid runId) =>
        string.Concat("dev-workflow-run-", runId.ToString("N"));
}

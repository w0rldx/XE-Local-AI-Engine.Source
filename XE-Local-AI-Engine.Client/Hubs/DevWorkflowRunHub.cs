namespace XE_Local_AI_Engine.Client.Hubs;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Common;
using XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1.Mappers;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;

/// <summary>
///     Operator-only live notifications for one development workflow run.
/// </summary>
/// <remarks>
///     Modelled on <see cref="WorkSessionHub" /> and explicitly NOT on a buffered per-run subscription hub: run events
///     are persisted append-only with a monotonic sequence and the persisted log IS the replay authority. Nor does a
///     disconnect cancel anything — a workflow run is durable and outlives both the browser tab and the engine, which
///     is the property this module exists to prove.
/// </remarks>
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = NodeAuthorizationPolicies.Operator)]
public sealed class DevWorkflowRunHub : Hub
{
    /// <summary>
    ///     How many persisted events one subscribe hands back. Past this the snapshot says so and the client pages the
    ///     event feed by <c>sinceSeq</c> — one extra round trip on a long-lived run, against an unbounded first frame
    ///     for every subscriber.
    /// </summary>
    private const int ReplayCap = 200;

    private readonly DevWorkflowOptions _options;
    private readonly DevWorkflowRunQueryService _queries;
    private readonly IDevWorkflowRunService _runs;

    public DevWorkflowRunHub(DevWorkflowRunQueryService queries, IDevWorkflowRunService runs, IOptions<DevWorkflowOptions> options)
    {
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        ArgumentNullException.ThrowIfNull(queries);
        _queries = queries;
        ArgumentNullException.ThrowIfNull(runs);
        _runs = runs;
    }

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
            detail = await _runs.GetAsync(runId, cancellationToken);
        }
        catch (DevWorkflowNotFoundException exception)
        {
            throw new HubException("Development workflow run was not found.", exception);
        }

        // Join BEFORE reading the replay: the other order leaves a window in which a change published between read and
        // join reaches nobody. The overlap is harmless — every push is an idempotent notification keyed by sequence.
        await Groups.AddToGroupAsync(Context.ConnectionId, DevWorkflowHubGroups.Run(runId), cancellationToken);

        var (events, truncated) = await ReplayWindow.ReadAsync(ReplayCap, limit => _queries.ListEventsAsync(runId, afterSeq, limit, cancellationToken));
        return new DevWorkflowRunSubscriptionSnapshot
        {
            RunId = runId,
            Status = detail.Run.Status.ToString(),
            QueuedNodeCount = detail.NodeRuns.Count(static nodeRun => nodeRun.Status == DevWorkflowNodeRunStatus.Queued),
            RunningNodeCount = detail.NodeRuns.Count(static nodeRun => nodeRun.Status == DevWorkflowNodeRunStatus.Running),
            PendingDecisionCount = detail.PendingDecisionCount,
            BlockingGateNodeRunId = detail.BlockingGateNodeRunId,
            LastSeq = detail.Run.LastSequence,
            Events = [.. events.Select(DevWorkflowContractMapper.ToResponse)],
            ReplayTruncated = truncated
        };
    }

    public Task UnsubscribeRun(Guid runId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, DevWorkflowHubGroups.Run(runId), Context.ConnectionAborted);
}

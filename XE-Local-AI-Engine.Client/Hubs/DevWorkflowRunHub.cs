namespace XE_Local_AI_Engine.Client.Hubs;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1;
using XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1.Mappers;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;

public static class DevWorkflowHubEvents
{
    public const string Changed = "devWorkflowChanged";
}

/// <summary>
///     What changed and where the run now stands.
/// </summary>
/// <remarks>
///     <see cref="Kind" /> is lowercase on the wire — the client switches on the literal. The payload deliberately
///     carries no content: the subscriber re-reads the named feed from its own watermark, so a dropped push degrades
///     to a late read rather than to a wrong render.
/// </remarks>
public sealed class DevWorkflowChanged
{
    public required Guid RunId { get; init; }

    public required long Seq { get; init; }

    public required string Kind { get; init; }
}

public sealed class DevWorkflowRunSubscriptionSnapshot
{
    public required Guid RunId { get; init; }

    public required string Status { get; init; }

    public required int QueuedNodeCount { get; init; }

    public required int RunningNodeCount { get; init; }

    public required int PendingDecisionCount { get; init; }

    public required Guid? BlockingGateNodeRunId { get; init; }

    public required long LastSeq { get; init; }

    public required IReadOnlyList<DevWorkflowRunEventResponse> Events { get; init; }

    public required bool ReplayTruncated { get; init; }
}

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
        catch (DevWorkflowNotFoundException)
        {
            throw new HubException("Development workflow run was not found.");
        }

        // Join BEFORE reading the replay: the other order leaves a window in which a change published between read and
        // join reaches nobody. The overlap is harmless — every push is an idempotent notification keyed by sequence.
        await Groups.AddToGroupAsync(Context.ConnectionId, DevWorkflowHubGroups.Run(runId), cancellationToken);

        // One over the cap, so "there is more" is observed rather than inferred from a full page.
        var events = await _queries.ListEventsAsync(runId, afterSeq, ReplayCap + 1, cancellationToken);
        return new DevWorkflowRunSubscriptionSnapshot
        {
            RunId = runId,
            Status = detail.Run.Status.ToString(),
            QueuedNodeCount = detail.NodeRuns.Count(static nodeRun => nodeRun.Status == DevWorkflowNodeRunStatus.Queued),
            RunningNodeCount = detail.NodeRuns.Count(static nodeRun => nodeRun.Status == DevWorkflowNodeRunStatus.Running),
            PendingDecisionCount = detail.PendingDecisionCount,
            BlockingGateNodeRunId = detail.BlockingGateNodeRunId,
            LastSeq = detail.Run.LastSequence,
            Events = [.. events.Take(ReplayCap).Select(DevWorkflowContractMapper.ToResponse)],
            ReplayTruncated = events.Count > ReplayCap
        };
    }

    public Task UnsubscribeRun(Guid runId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, DevWorkflowHubGroups.Run(runId), Context.ConnectionAborted);
}

internal static class DevWorkflowHubGroups
{
    public static string Run(Guid runId) =>
        string.Concat("dev-workflow-run-", runId.ToString("N"));
}

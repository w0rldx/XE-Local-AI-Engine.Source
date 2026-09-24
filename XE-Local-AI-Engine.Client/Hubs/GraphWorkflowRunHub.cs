namespace XE_Local_AI_Engine.Client.Hubs;

using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1;
using XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1.Mappers;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;

public static class GraphWorkflowHubEvents
{
    public const string Changed = "graphWorkflowChanged";
}

/// <summary>
///     What changed and where the run now stands.
/// </summary>
/// <remarks>
///     <see cref="Kind" /> is lowercase on the wire — the client switches on the literal. The payload deliberately
///     carries no content: the subscriber re-reads the named feed from its own watermark, so a dropped push degrades
///     to a late read rather than to a wrong render.
/// </remarks>
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

    /// <summary>Parked rows waiting on an operator's Approve/Reject (a <c>Pause</c>).</summary>
    public required int PendingDecisions { get; init; }

    /// <summary>Parked rows waiting on the chat user's answer (a <c>ChatInput</c>).</summary>
    public required int PendingInputs { get; init; }

    public required long LastSeq { get; init; }

    public required IReadOnlyList<GraphWorkflowRunEventResponse> Events { get; init; }

    public required bool ReplayTruncated { get; init; }
}

/// <summary>
///     Operator-only live notifications for one graph workflow run.
/// </summary>
/// <remarks>
///     Modelled on <see cref="DevWorkflowRunHub" /> and explicitly NOT on a buffered per-run subscription hub: run
///     events are persisted append-only with a monotonic sequence and the persisted log IS the replay authority. Nor
///     does a disconnect cancel anything — a workflow run is durable and outlives both the browser tab and the engine,
///     which is the property this module exists to prove.
/// </remarks>
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = NodeAuthorizationPolicies.Operator)]
public sealed class GraphWorkflowRunHub : Hub
{
    private readonly GraphWorkflowOptions _options;
    private readonly IInvocationResumeRegistry _resumeRegistry;
    private readonly IGraphWorkflowRunService _runs;

    public GraphWorkflowRunHub(IGraphWorkflowRunService runs,
        IInvocationResumeRegistry resumeRegistry,
        IOptions<GraphWorkflowOptions> options)
    {
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(resumeRegistry);
        _runs = runs;
        _resumeRegistry = resumeRegistry;
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

        // Join BEFORE reading the replay: the other order leaves a window in which a change published between read and
        // join reaches nobody. The overlap is harmless — every push is an idempotent notification keyed by sequence.
        await Groups.AddToGroupAsync(Context.ConnectionId, GraphWorkflowHubGroups.Run(runId), cancellationToken);

        // The same paged read the event endpoint answers with, at the same window: neither side owns that arithmetic, so a client moves between subscription and feed without a gap or a repeat.
        // The watermark is the last row actually HANDED over, never the run's own sequence: on a truncated page that is past events nothing replays again.
        var replay = await _runs.ListEventsAsync(runId, afterSeq, cancellationToken);
        return new GraphWorkflowRunSubscriptionSnapshot
        {
            RunId = runId,
            Status = detail.Run.Status.ToString(),
            QueuedNodeCount = detail.NodeRuns.Count(static nodeRun => nodeRun.Status == GraphWorkflowNodeRunStatus.Queued),
            RunningNodeCount = detail.NodeRuns.Count(static nodeRun => nodeRun.Status == GraphWorkflowNodeRunStatus.Running),
            // Split by the pending ACT the row names, not by the graph: the row carries it precisely so a reader need not parse the graph.
            PendingDecisions = detail.NodeRuns.Count(static nodeRun => nodeRun is { Status: GraphWorkflowNodeRunStatus.WaitingForApproval }
                                                                       && nodeRun.PendingDecisionKind != GraphWorkflowDecisionKind.Answer),
            PendingInputs = detail.NodeRuns.Count(static nodeRun => nodeRun is { Status: GraphWorkflowNodeRunStatus.WaitingForApproval, PendingDecisionKind: GraphWorkflowDecisionKind.Answer }),
            LastSeq = replay.LastSeq,
            Events = [.. replay.Events.Select(static @event => @event.ToResponse())],
            ReplayTruncated = replay.ReplayTruncated
        };
    }

    /// <summary>The live text of one RUNNING node's turn: the resume registry's snapshot, deltas and terminal event.</summary>
    /// <remarks>
    ///     Served straight from <see cref="IInvocationResumeRegistry.ResumeAsync" /> and deliberately NOT tracked as a
    ///     chat attachment: the graph turn has no chat client to detach from, so the reaper must never see one. The
    ///     registry is in memory; after a restart the row is failed <c>Interrupted</c> and nothing is left to stream.
    /// </remarks>
    public async IAsyncEnumerable<ChatStreamEvent> StreamNodeActivity(Guid runId,
        string nodeKey,
        [EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            throw new HubException("Graph workflows are disabled on this node.");
        }

        if (runId == Guid.Empty || string.IsNullOrWhiteSpace(nodeKey))
        {
            throw new HubException("Graph workflow run id and node key are required.");
        }

        GraphWorkflowRunDetail detail;
        try
        {
            detail = await _runs.GetRunAsync(runId, cancellationToken);
        }
        catch (GraphWorkflowNotFoundException)
        {
            throw new HubException("Graph workflow run was not found.");
        }

        var nodeRun = detail.NodeRuns.FirstOrDefault(row => string.Equals(row.NodeKey, nodeKey, StringComparison.Ordinal))
                      ?? throw new HubException("Graph workflow node is not part of this run.");
        if (nodeRun.Status != GraphWorkflowNodeRunStatus.Running)
        {
            throw new HubException("Graph workflow node is not running.");
        }

        if (nodeRun.InvocationId is not { } invocationId)
        {
            throw new HubException("Graph workflow node has no live turn.");
        }

        IAsyncEnumerable<ChatStreamEvent> stream;
        try
        {
            stream = _resumeRegistry.ResumeAsync(invocationId, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            throw new HubException("Graph workflow node turn is no longer live.");
        }

        await foreach (var @event in stream.WithCancellation(cancellationToken))
        {
            yield return @event;
        }
    }

    public Task UnsubscribeRun(Guid runId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, GraphWorkflowHubGroups.Run(runId), Context.ConnectionAborted);
}

internal static class GraphWorkflowHubGroups
{
    public static string Run(Guid runId) =>
        string.Concat("graph-workflow-run-", runId.ToString("N"));
}

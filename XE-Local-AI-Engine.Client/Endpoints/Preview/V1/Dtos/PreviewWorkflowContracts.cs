namespace XE_Local_AI_Engine.Client.Endpoints.Preview.V1;

using XE_Local_AI_Engine.Client.Services.PreviewWorkflows;

/// <summary>Route binding for endpoints addressing a workflow by id.</summary>
public sealed class PreviewWorkflowRouteRequest
{
    public Guid WorkflowId { get; init; }
}

/// <summary>Route binding for endpoints addressing a run by id.</summary>
public sealed class PreviewRunRouteRequest
{
    public Guid RunId { get; init; }
}

/// <summary>Body for <c>POST preview/workflows</c>.</summary>
public sealed class CreatePreviewWorkflowRequest
{
    public required string Name { get; init; }

    public required PreviewWorkflowGraph Graph { get; init; }
}

/// <summary>Body for <c>PUT preview/workflows/{workflowId}</c>. <see cref="Version" /> drives optimistic concurrency (409).</summary>
public sealed class UpdatePreviewWorkflowRequest
{
    public Guid WorkflowId { get; init; }

    public required string Name { get; init; }

    public required PreviewWorkflowGraph Graph { get; init; }

    public required int Version { get; init; }
}

/// <summary>Body for <c>POST preview/runs/execute</c> (unsaved inline graph; persists nothing).</summary>
public sealed class ExecuteUnsavedPreviewWorkflowRequest
{
    public required PreviewWorkflowGraph Graph { get; init; }
}

/// <summary>List-row projection (no graph).</summary>
public sealed record PreviewWorkflowSummaryResponse(Guid Id, string Name, int Version, long CreatedAtUtc, long UpdatedAtUtc);

/// <summary>List response.</summary>
public sealed class ListPreviewWorkflowsResponse
{
    public required IReadOnlyList<PreviewWorkflowSummaryResponse> Items { get; init; }
}

/// <summary>Full workflow including the deserialized graph.</summary>
public sealed record PreviewWorkflowResponse(
    Guid Id,
    string Name,
    PreviewWorkflowGraph Graph,
    int Version,
    long CreatedAtUtc,
    long UpdatedAtUtc);

/// <summary>Execute response — the new run id the client subscribes to over the hub.</summary>
public sealed record PreviewRunStartedResponse(Guid RunId);

/// <summary>
///     One run as seen by the discovery endpoints. <c>IsLive</c> is true while the run still holds a concurrency slot;
///     a false value means the run is terminal but its event log is still retained, so a reattaching client can replay
///     the result. <c>LastSeq</c> is the highest buffered sequence number, which a client passes back as the hub's
///     <c>afterSeq</c> to receive only what it has not already applied.
/// </summary>
public sealed record PreviewRunSummaryResponse(
    Guid RunId,
    string State,
    bool IsLive,
    long StartedAtUtc,
    long LastSeq,
    int SubscriberCount,
    string? PausedNodeId,
    string? PauseRequestId);

/// <summary>Response for <c>GET preview/runs</c>.</summary>
public sealed class ListPreviewRunsResponse
{
    public required IReadOnlyList<PreviewRunSummaryResponse> Items { get; init; }
}

/// <summary>Response for <c>POST preview/runs/cancel-all</c> — how many live runs were cancelled.</summary>
public sealed record CancelAllPreviewRunsResponse(int CancelledCount);


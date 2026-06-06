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

/// <summary>Maps Application contracts to wire responses.</summary>
internal static class PreviewWorkflowResponseMapper
{
    public static PreviewWorkflowSummaryResponse ToResponse(this PreviewWorkflowSummary summary)
    {
        return new PreviewWorkflowSummaryResponse(summary.Id, summary.Name, summary.Version, summary.CreatedAtUtc, summary.UpdatedAtUtc);
    }

    public static PreviewWorkflowResponse ToResponse(this PreviewWorkflowDetail detail)
    {
        return new PreviewWorkflowResponse(detail.Id, detail.Name, detail.Graph, detail.Version, detail.CreatedAtUtc, detail.UpdatedAtUtc);
    }
}

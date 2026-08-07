namespace XE_Local_AI_Engine.Client.Endpoints.Preview.V1.Mappers;

using XE_Local_AI_Engine.Client.Services.PreviewWorkflows;

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

    public static PreviewRunSummaryResponse ToResponse(this PreviewRunSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        // State goes over the wire as its enum NAME so the client reads a stable string, matching how the graph's
        // node kinds serialize.
        return new PreviewRunSummaryResponse(snapshot.RunId,
            snapshot.State.ToString(),
            snapshot.IsLive,
            snapshot.StartedAtUtc,
            snapshot.LastSeq,
            snapshot.SubscriberCount,
            snapshot.PausedNodeId,
            snapshot.PauseRequestId);
    }
}

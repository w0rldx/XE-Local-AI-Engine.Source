namespace XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1.Mappers;

using System.Text.Json;
using System.Text.Json.Serialization;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Projects the store's snapshots onto the wire contracts. Entities never reach an endpoint: their text columns
///     are encrypted at rest, so a mapper reading one would hand the operator ciphertext.
///     <para>
///         The graph crosses in BOTH directions here, and deliberately as a deserialize-and-reserialize of the same
///         field list rather than a projection: a definition read back, edited and saved has to keep every field it
///         arrived with, and a run's pinned graph has to render exactly what the dispatcher routes on.
///     </para>
/// </summary>
internal static class DevWorkflowContractMapper
{
    /// <summary>
    ///     camelCase and nulls omitted — the shape the runtime's own parser reads, so a graph that survives a
    ///     round-trip through this mapper is one it still accepts.
    /// </summary>
    private static readonly JsonSerializerOptions GraphOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    ///     The stored graph document as the wire shape. Defaults are filled in the same way the runtime's parser fills
    ///     them, so the rendered graph says what the dispatcher will actually do: an unnamed node is labelled by its
    ///     key, and an absent edge list is no edges rather than null.
    /// </summary>
    public static DevWorkflowGraph ToWireGraph(string graphJson)
    {
        var graph = JsonSerializer.Deserialize<DevWorkflowGraph>(graphJson, GraphOptions) ?? DevWorkflowGraph.Empty;
        return graph with
        {
            SchemaVersion = graph.SchemaVersion == 0 ? 1 : graph.SchemaVersion,
            Nodes =
            [
                .. (graph.Nodes ?? []).Select(static node => node with
                {
                    Label = string.IsNullOrWhiteSpace(node.Label) ? node.NodeKey : node.Label
                })
            ],
            Edges = graph.Edges ?? []
        };
    }

    public static string ToGraphJson(DevWorkflowGraph graph) =>
        JsonSerializer.Serialize(graph, GraphOptions);

    public static DevWorkflowWorkItemResponse ToResponse(this DevWorkflowWorkItemSnapshot value, IReadOnlyList<DevWorkflowRunSummary> runs) =>
        new(value.Id,
            value.Title,
            value.Request,
            value.DevelopmentProjectId,
            value.Status.ToString(),
            value.LatestRunId,
            [.. runs.Select(ToResponse)],
            value.CreatedAtUtc,
            value.UpdatedAtUtc,
            value.Version);

    public static DevWorkflowWorkItemSummaryResponse ToSummaryResponse(this DevWorkflowWorkItemSnapshot value) =>
        new(value.Id,
            value.Title,
            value.DevelopmentProjectId,
            value.Status.ToString(),
            value.LatestRunId,
            value.LatestRunStatus?.ToString(),
            value.LatestRunDefinitionName,
            value.LatestRunNodes.Queued,
            value.LatestRunNodes.Running,
            value.LatestRunNodes.Completed,
            value.LatestRunNodes.Total,
            value.UpdatedAtUtc);

    public static DevWorkflowRunSummaryResponse ToResponse(this DevWorkflowRunSummary value) =>
        new(value.Id,
            value.WorkItemId,
            value.DefinitionId,
            value.DefinitionName,
            value.Status.ToString(),
            value.Nodes.Queued,
            value.Nodes.Running,
            value.Nodes.Completed,
            value.Nodes.Total,
            value.Nodes.PendingDecisionCount,
            value.Nodes.BlockingGateNodeRunId,
            value.StartedAtUtc,
            value.UpdatedAtUtc);

    public static DevWorkflowDefinitionResponse ToResponse(this DevWorkflowDefinitionSnapshot value) =>
        new(value.Id,
            value.Name,
            ToWireGraph(value.GraphJson),
            value.GraphHash,
            value.Source.ToString(),
            value.SeedSlug,
            value.Archived,
            value.Version,
            value.CreatedAtUtc,
            value.UpdatedAtUtc);

    public static DevWorkflowDefinitionSummaryResponse ToResponse(this DevWorkflowDefinitionSummary value) =>
        new(value.Id, value.Name, value.Source.ToString(), value.SeedSlug, value.Archived, value.Version, value.NodeCount, value.UpdatedAtUtc);

    public static DevWorkflowRunEventResponse ToResponse(this DevWorkflowRunEventSnapshot value) =>
        new(value.Id, value.Sequence, value.EventType, value.NodeRunId, value.Outcome, value.DetailJson, value.OperationId, value.OccurredAtUtc);

    public static DevWorkflowArtifactResponse ToResponse(this DevWorkflowArtifactSnapshot value) =>
        new(value.Id,
            value.LineageId,
            value.Version,
            value.Sequence,
            value.Kind.ToString(),
            value.Name,
            value.MediaType,
            value.ContentSha256,
            value.SizeBytes,
            value.ProducedByNodeRunId,
            value.ProducingNodeKey,
            value.IsValid,
            value.IsStale,
            value.StaleBecauseArtifactId,
            value.StaleReason,
            value.IsLatest,
            value.CreatedAtUtc);

    public static DevWorkflowDecisionResponse ToResponse(this DevWorkflowDecisionSnapshot value) =>
        new(value.Id,
            value.NodeRunId,
            value.Attempt,
            value.Decision.ToString(),
            value.Comment,
            value.DecidedBySubject,
            value.DecidedAtUtc,
            value.OperationId,
            value.Sequence);

    /// <summary>
    ///     The page's HIGHEST sequence rather than its last row's. Feeds are ordered for reading, not by watermark, so
    ///     the newest sequence can sit anywhere in the page; paging from the last row would replay rows forever.
    /// </summary>
    public static long HighestSequence(IEnumerable<long> sequences)
    {
        var highest = 0L;
        foreach (var sequence in sequences)
        {
            highest = Math.Max(highest, sequence);
        }

        return highest;
    }
}

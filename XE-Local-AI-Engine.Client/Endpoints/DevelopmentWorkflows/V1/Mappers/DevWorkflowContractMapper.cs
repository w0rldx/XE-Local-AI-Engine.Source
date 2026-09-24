namespace XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1.Mappers;

using System.Text.Json;
using System.Text.Json.Serialization;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;

/// <summary>
///     Projects the store's snapshots onto the wire contracts.
/// </summary>
/// <remarks>
///     Entities never reach an endpoint: their text columns are encrypted at rest, so a mapper reading one would hand
///     the operator ciphertext. The graph crosses in BOTH directions here, and deliberately as a
///     deserialize-and-reserialize of the same field list rather than a projection: a definition read back, edited and
///     saved has to keep every field it arrived with, and a run's pinned graph has to render exactly what the
///     dispatcher routes on.
/// </remarks>
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

    /// <summary>The stored graph document as the wire shape.</summary>
    /// <remarks>
    ///     Defaults are filled in the same way the runtime's parser fills them, so the rendered graph says what the
    ///     dispatcher will actually do: an unnamed node is labelled by its key, and an absent edge list is no edges
    ///     rather than null.
    /// </remarks>
    public static DevWorkflowGraph ToWireGraph(string graphJson)
    {
        var graph = JsonSerializer.Deserialize<DevWorkflowGraph>(graphJson, GraphOptions) ?? DevWorkflowGraph.Empty;

        // Asked of the runtime's parser, not walked again here: a second implementation of "which nodes are template clones-in-waiting" is the one thing this class exists to
        // prevent, and the parser answers empty for a graph it cannot route rather than throwing on the read path.
        var templates = DevWorkflowGraphContract.TemplateNodeKeys(graphJson);
        return graph with
        {
            SchemaVersion = graph.SchemaVersion == 0 ? 1 : graph.SchemaVersion,
            Nodes =
            [
                .. (graph.Nodes ?? []).Select(node => node with
                {
                    Label = string.IsNullOrWhiteSpace(node.Label) ? node.NodeKey : node.Label,
                    IsTemplate = templates.Contains(node.NodeKey)
                })
            ],
            Edges = graph.Edges ?? []
        };
    }

    /// <summary>The wire graph as the document that gets stored.</summary>
    /// <remarks>
    ///     Two fields are touched on the way in: <c>toolMode</c> is written in the parser's own spelling, so a
    ///     definition saved as <c>"apply"</c> stores <c>"Apply"</c> and every reader of the blob — including one that
    ///     does not parse case-insensitively — sees one form; and <c>isTemplate</c> is dropped, because it is DERIVED
    ///     on the way out and a stored copy would be a second answer able to disagree with the parser's.
    /// </remarks>
    public static string ToGraphJson(DevWorkflowGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        return JsonSerializer.Serialize(graph with
        {
            Nodes =
            [
                .. (graph.Nodes ?? []).Select(static node => node with
                {
                    ToolMode = DevWorkflowGraphContract.CanonicalToolMode(node.ToolMode),
                    IsTemplate = null
                })
            ]
        }, GraphOptions);
    }

    public static DevWorkflowWorkItemResponse ToResponse(this DevWorkflowWorkItemSnapshot value, IReadOnlyList<DevWorkflowRunSummary> runs) =>
        new()
        {
            Id = value.Id,
            Title = value.Title,
            Request = value.Request,
            DevelopmentProjectId = value.DevelopmentProjectId,
            Status = value.Status.ToString(),
            LatestRunId = value.LatestRunId,
            Runs = [.. runs.Select(ToResponse)],
            CreatedAtUtc = value.CreatedAtUtc,
            UpdatedAtUtc = value.UpdatedAtUtc,
            Version = value.Version
        };

    public static DevWorkflowWorkItemSummaryResponse ToSummaryResponse(this DevWorkflowWorkItemSnapshot value) =>
        new()
        {
            Id = value.Id,
            Title = value.Title,
            DevelopmentProjectId = value.DevelopmentProjectId,
            Status = value.Status.ToString(),
            LatestRunId = value.LatestRunId,
            LatestRunStatus = value.LatestRunStatus?.ToString(),
            DefinitionName = value.LatestRunDefinitionName,
            QueuedNodeCount = value.LatestRunNodes.Queued,
            RunningNodeCount = value.LatestRunNodes.Running,
            CompletedNodeCount = value.LatestRunNodes.Completed,
            TotalNodeCount = value.LatestRunNodes.Total,
            UpdatedAtUtc = value.UpdatedAtUtc
        };

    public static DevWorkflowRunSummaryResponse ToResponse(this DevWorkflowRunSummary value) =>
        new()
        {
            Id = value.Id,
            WorkItemId = value.WorkItemId,
            DefinitionId = value.DefinitionId,
            DefinitionName = value.DefinitionName,
            Status = value.Status.ToString(),
            QueuedNodeCount = value.Nodes.Queued,
            RunningNodeCount = value.Nodes.Running,
            CompletedNodeCount = value.Nodes.Completed,
            TotalNodeCount = value.Nodes.Total,
            PendingDecisionCount = value.Nodes.PendingDecisionCount,
            BlockingGateNodeRunId = value.Nodes.BlockingGateNodeRunId,
            StartedAtUtc = value.StartedAtUtc,
            UpdatedAtUtc = value.UpdatedAtUtc
        };

    public static DevWorkflowDefinitionResponse ToResponse(this DevWorkflowDefinitionSnapshot value) =>
        new()
        {
            Id = value.Id,
            Name = value.Name,
            Graph = ToWireGraph(value.GraphJson),
            GraphHash = value.GraphHash,
            Source = value.Source.ToString(),
            SeedSlug = value.SeedSlug,
            Archived = value.Archived,
            Version = value.Version,
            CreatedAtUtc = value.CreatedAtUtc,
            UpdatedAtUtc = value.UpdatedAtUtc
        };

    public static DevWorkflowDefinitionSummaryResponse ToResponse(this DevWorkflowDefinitionSummary value) =>
        new()
        {
            Id = value.Id,
            Name = value.Name,
            Source = value.Source.ToString(),
            SeedSlug = value.SeedSlug,
            Archived = value.Archived,
            Version = value.Version,
            NodeCount = value.NodeCount,
            UpdatedAtUtc = value.UpdatedAtUtc
        };

    public static DevWorkflowRuleSetResponse ToResponse(this DevWorkflowRuleSetSnapshot value) =>
        new()
        {
            Id = value.Id,
            Name = value.Name,
            Description = value.Description,
            Body = value.Body,
            Scope = ToScope(value.ScopeJson),
            Enabled = value.Enabled,
            ContentSha256 = value.ContentSha256,
            Version = value.Version,
            CreatedAtUtc = value.CreatedAtUtc,
            UpdatedAtUtc = value.UpdatedAtUtc
        };

    public static DevWorkflowRuleSetSummaryResponse ToResponse(this DevWorkflowRuleSetSummary value) =>
        new()
        {
            Id = value.Id,
            Name = value.Name,
            Description = value.Description,
            Scope = ToScope(value.ScopeJson),
            Enabled = value.Enabled,
            ContentSha256 = value.ContentSha256,
            Version = value.Version,
            CreatedAtUtc = value.CreatedAtUtc,
            UpdatedAtUtc = value.UpdatedAtUtc
        };

    /// <summary>
    ///     The scope as the resolver stores it. An omitted scope is BOTH axes empty, which is the document's own
    ///     spelling of "applies everywhere" — so the column always holds a scope the resolver can read.
    /// </summary>
    public static string ToScopeJson(DevWorkflowRuleScope? scope) =>
        JsonSerializer.Serialize(new DevWorkflowRuleScope(scope?.ProjectIds ?? [], scope?.NodeTypes ?? []), GraphOptions);

    /// <summary>
    ///     The stored scope as the wire shape, read through the runtime's own parser so the page renders exactly the
    ///     axes the resolver matches on.
    /// </summary>
    /// <remarks>
    ///     A column nothing can parse renders as empty axes rather than failing the read: the resolver already treats
    ///     that row as applying to NOTHING, and a management page that cannot load it is a page nobody can use to fix
    ///     it.
    /// </remarks>
    private static DevWorkflowRuleScope ToScope(string scopeJson) =>
        DevWorkflowRulePolicyResolver.ReadScope(scopeJson) is { } scope ? new DevWorkflowRuleScope(scope.ProjectIds, scope.NodeTypes) : new DevWorkflowRuleScope([], []);

    public static DevWorkflowRunEventResponse ToResponse(this DevWorkflowRunEventSnapshot value) =>
        new()
        {
            Id = value.Id,
            Sequence = value.Sequence,
            EventType = value.EventType,
            NodeRunId = value.NodeRunId,
            Outcome = value.Outcome,
            DetailJson = value.DetailJson,
            OperationId = value.OperationId,
            OccurredAtUtc = value.OccurredAtUtc
        };

    public static DevWorkflowArtifactResponse ToResponse(this DevWorkflowArtifactSnapshot value) =>
        new()
        {
            Id = value.Id,
            LineageId = value.LineageId,
            Version = value.Version,
            Sequence = value.Sequence,
            Kind = value.Kind.ToString(),
            Name = value.Name,
            MediaType = value.MediaType,
            ContentSha256 = value.ContentSha256,
            SizeBytes = value.SizeBytes,
            ProducedByNodeRunId = value.ProducedByNodeRunId,
            ProducingNodeKey = value.ProducingNodeKey,
            IsValid = value.IsValid,
            IsStale = value.IsStale,
            StaleBecauseArtifactId = value.StaleBecauseArtifactId,
            StaleReason = value.StaleReason,
            IsLatest = value.IsLatest,
            CreatedAtUtc = value.CreatedAtUtc
        };

    public static DevWorkflowDecisionResponse ToResponse(this DevWorkflowDecisionSnapshot value) =>
        new()
        {
            Id = value.Id,
            NodeRunId = value.NodeRunId,
            Attempt = value.Attempt,
            Decision = value.Decision.ToString(),
            Comment = value.Comment,
            DecidedBySubject = value.DecidedBySubject,
            DecidedAtUtc = value.DecidedAtUtc,
            OperationId = value.OperationId,
            Sequence = value.Sequence
        };

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

namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1;

using System.Text.Json.Serialization;
using XE_Local_AI_Engine.Client.Services.Monitoring;

/// <summary>Request for one agent's read-only playbook cohort monitoring. The agent id travels in the route.</summary>
public sealed class GetAgentPlaybookMonitorRequest
{
    public Guid AgentDefinitionId { get; init; }
}

/// <summary>
///     Wire projection of one <see cref="PlaybookActionMonitorView" />. <see cref="Status" /> serializes as
///     its string name via the globally registered <c>JsonStringEnumConverter</c>; the remaining fields serialize
///     camelCase.
/// </summary>
public sealed class PlaybookActionMonitorItemResponse
{
    public required Guid ActionId { get; init; }

    public required long EnabledAtUtc { get; init; }

    public required double BeforeDownRate { get; init; }

    public required double AfterDownRate { get; init; }

    public required int AfterSampleSize { get; init; }

    public required PlaybookMonitorStatus Status { get; init; }

    public required bool Flagged { get; init; }

    public required string? FacetToolName { get; init; }
}

/// <summary>
///     The relevance-retrieval thresholds surfaced alongside the monitor view, plus the active ranker, which the panel
///     renders as the "injection is relevance-gated — top-{topK} of N actions, ranked by …" banner.
/// </summary>
/// <remarks>
///     <see cref="Ranker" /> is the literal lowercase string "embedding" or "lexical", matching the React Zod enum.
///     <see cref="EmbeddingModel" /> carries the configured node-local embedding model when the embedding ranker is
///     active and is omitted (via <see cref="JsonIgnoreCondition.WhenWritingNull" />) when lexical, so the React Zod
///     optional matches. All fields serialize camelCase.
/// </remarks>
public sealed class PlaybookRetrievalResponse
{
    public required int Threshold { get; init; }

    public required int TopK { get; init; }

    public required string Ranker { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EmbeddingModel { get; init; }
}

/// <summary>
///     Read-only playbook monitoring envelope for one agent: one item per Enabled action that carries an enable
///     timestamp, plus the current relevance-retrieval thresholds. All fields serialize camelCase.
/// </summary>
public sealed class AgentPlaybookMonitorResponse
{
    public required IReadOnlyList<PlaybookActionMonitorItemResponse> Items { get; init; }

    public required PlaybookRetrievalResponse Retrieval { get; init; }
}

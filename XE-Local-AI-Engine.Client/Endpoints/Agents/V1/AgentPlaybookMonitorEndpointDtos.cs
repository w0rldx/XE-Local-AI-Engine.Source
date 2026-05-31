namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1;

using XE_Local_AI_Engine.Client.Services.Monitoring;

/// <summary>Request for one agent's read-only playbook cohort monitoring (Playbook P5). The agent id travels in the route.</summary>
public sealed class GetAgentPlaybookMonitorRequest
{
    public Guid AgentDefinitionId { get; init; }
}

/// <summary>
///     Wire projection of one <see cref="PlaybookActionMonitorView" /> (Playbook P5). <see cref="Status" /> serializes as
///     its string name via the globally registered <c>JsonStringEnumConverter</c>; the remaining fields serialize
///     camelCase. A positional record so the analyzer does not flag unused init-only auto-properties (S3459/S1144).
/// </summary>
public sealed record PlaybookActionMonitorItemResponse(
    Guid ActionId,
    long EnabledAtUtc,
    double BeforeDownRate,
    double AfterDownRate,
    int AfterSampleSize,
    PlaybookMonitorStatus Status,
    bool Flagged,
    string? FacetToolName);

/// <summary>
///     The relevance-retrieval gating thresholds surfaced alongside the monitor view (Playbook P5, plan §3.3/§4.2). The
///     panel uses these to render the "injection is relevance-gated — top-{topK} of N actions" banner.
/// </summary>
public sealed record PlaybookRetrievalResponse(int Threshold, int TopK);

/// <summary>
///     Read-only playbook monitoring envelope for one agent (Playbook P5): one item per Enabled action that carries an
///     enable timestamp, plus the current relevance-retrieval thresholds. All fields serialize camelCase.
/// </summary>
public sealed record AgentPlaybookMonitorResponse(
    IReadOnlyList<PlaybookActionMonitorItemResponse> Items,
    PlaybookRetrievalResponse Retrieval);

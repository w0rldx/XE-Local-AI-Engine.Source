namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1;

using XE_Local_AI_Engine.Client.Persistence;

/// <summary>List request for one agent's playbook actions. The agent id travels in the route.</summary>
public sealed class ListAgentPlaybookActionsRequest
{
    public Guid AgentDefinitionId { get; init; }
}

/// <summary>
///     Create request for a playbook action. The owning agent id travels in the route; the body carries the editable
///     fields, mirroring <see cref="PlaybookActionInput" /> (minus the route-bound agent id).
/// </summary>
public sealed class CreatePlaybookActionRequest
{
    public Guid AgentDefinitionId { get; init; }

    public PlaybookActionState State { get; init; } = PlaybookActionState.Enabled;

    public string? TriggerCondition { get; init; }

    public string? Behavior { get; init; }

    public string? Scope { get; init; }

    public int Priority { get; init; }
}

/// <summary>
///     Update request for a playbook action (also drives enable/disable via <see cref="State" /> and reorder via
///     <see cref="Priority" />). The owning agent id and the action id both travel in the route.
/// </summary>
public sealed class UpdatePlaybookActionRequest
{
    public Guid AgentDefinitionId { get; init; }

    public Guid ActionId { get; init; }

    public PlaybookActionState State { get; init; } = PlaybookActionState.Enabled;

    public string? TriggerCondition { get; init; }

    public string? Behavior { get; init; }

    public string? Scope { get; init; }

    public int Priority { get; init; }
}

public sealed class DeletePlaybookActionRequest
{
    public Guid AgentDefinitionId { get; init; }

    public Guid ActionId { get; init; }
}

/// <summary>
///     Wire projection of a stored playbook action. <see cref="State" />/<see cref="Source" /> serialize as their
///     string names via the globally registered <c>JsonStringEnumConverter</c>; the remaining fields serialize camelCase.
/// </summary>
public sealed class PlaybookActionResponse
{
    public required Guid Id { get; init; }

    public required Guid AgentDefinitionId { get; init; }

    public required PlaybookActionState State { get; init; }

    public required PlaybookActionSource Source { get; init; }

    public string? TriggerCondition { get; init; }

    public required string Behavior { get; init; }

    public string? Scope { get; init; }

    public required int Priority { get; init; }

    public required int Version { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }

    /// <summary>Analysis-staging provenance for an analysis-proposed action — the feedback ids that drove it. Null for manual actions.</summary>
    public IReadOnlyList<Guid>? SourceFeedbackIds { get; init; }

    /// <summary>Analysis-agent confidence in [0,1]. Null for manual actions.</summary>
    public double? Confidence { get; init; }

    /// <summary>
    ///     Latest golden-conversation eval outcome (pass/fail + counts + per-case results). Null until an eval has run
    ///     and after the action is edited (a stale pass is cleared). The promote gate enables the action only when this
    ///     is present, passed and current.
    /// </summary>
    public PlaybookEvalResultResponse? EvalResult { get; init; }
}

/// <summary>
///     Wire projection of <c>PlaybookAction.EvalResult</c>: ids + pass/fail flags + counts only (no
///     transcripts). Field names match the persisted camelCase JSON so the React Zod schema parses the same shape.
/// </summary>
public sealed record PlaybookEvalResultResponse(
    bool Passed,
    long EvaluatedAtUtc,
    int ActionVersionAtEval,
    string ModelName,
    int GoldenCaseCount,
    int GoldenCaseTotal,
    int BaselinePassCount,
    int CandidatePassCount,
    int RegressedCaseCount,
    int ImprovedCaseCount,
    IReadOnlyList<PlaybookEvalCaseResultResponse> Cases);

/// <summary>Per-case outcome inside a <see cref="PlaybookEvalResultResponse" />.</summary>
public sealed record PlaybookEvalCaseResultResponse(
    Guid GoldenCaseId,
    string ScoredBy,
    bool BaselinePass,
    bool CandidatePass,
    bool Regressed);

/// <summary>
///     409 body when golden-conversation evaluation blocks a promote: <see cref="Status" /> is the
///     <c>PlaybookPromotionStatus</c> enum name (<c>EvalRequired</c> / <c>EvalRegressed</c> / <c>EvalStale</c>),
///     <see cref="Reason" /> is a short human message the panel renders.
/// </summary>
public sealed record PlaybookPromotionConflictResponse(string Status, string Reason);

public sealed class ListPlaybookActionsResponse
{
    public required IReadOnlyList<PlaybookActionResponse> Items { get; init; }
}

/// <summary>Route request for running analysis over one agent's feedback. The agent id travels in the route.</summary>
public sealed class AnalyzePlaybookRequest
{
    public Guid AgentDefinitionId { get; init; }
}

/// <summary>
///     Route request for a Suggested-action review transition (promote/reject). Both the owning agent id and the action
///     id travel in the route; there is no body.
/// </summary>
public sealed class SuggestedPlaybookActionRouteRequest
{
    public Guid AgentDefinitionId { get; init; }

    public Guid ActionId { get; init; }
}

/// <summary>Edit request for a pending Suggested action. It stays Suggested/Analysis and keeps its evidence.</summary>
public sealed class UpdateSuggestedPlaybookActionRequest
{
    public Guid AgentDefinitionId { get; init; }

    public Guid ActionId { get; init; }

    public string? Behavior { get; init; }

    public string? TriggerCondition { get; init; }

    public string? Scope { get; init; }

    public int Priority { get; init; }
}

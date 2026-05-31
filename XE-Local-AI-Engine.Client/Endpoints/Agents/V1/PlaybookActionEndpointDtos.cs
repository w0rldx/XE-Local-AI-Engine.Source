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

    /// <summary>Playbook P3: provenance for an analysis-proposed action — the feedback ids that drove it. Null for manual actions.</summary>
    public IReadOnlyList<Guid>? SourceFeedbackIds { get; init; }

    /// <summary>Playbook P3: analysis-agent confidence in [0,1]. Null for manual actions.</summary>
    public double? Confidence { get; init; }
}

public sealed class ListPlaybookActionsResponse
{
    public required IReadOnlyList<PlaybookActionResponse> Items { get; init; }
}

/// <summary>Route request for running analysis over one agent's feedback (Playbook P3). The agent id travels in the route.</summary>
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

/// <summary>Edit request for a pending Suggested action (Playbook P3). It stays Suggested/Analysis and keeps its evidence.</summary>
public sealed class UpdateSuggestedPlaybookActionRequest
{
    public Guid AgentDefinitionId { get; init; }

    public Guid ActionId { get; init; }

    public string? Behavior { get; init; }

    public string? TriggerCondition { get; init; }

    public string? Scope { get; init; }

    public int Priority { get; init; }
}

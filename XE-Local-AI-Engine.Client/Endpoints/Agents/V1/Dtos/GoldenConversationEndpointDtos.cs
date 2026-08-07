namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1;

/// <summary>One conversation turn in a golden case: a role (<c>user</c>/<c>assistant</c>) and its text.</summary>
public sealed record GoldenTurnDto(string Role, string Text);

/// <summary>
///     Deterministic assertion for golden-conversation scoring: the candidate output must contain every
///     <see cref="RequiredPhrases" /> and none of the <see cref="ForbiddenPhrases" />.
/// </summary>
public sealed record GoldenAssertionDto(
    IReadOnlyList<string> RequiredPhrases,
    IReadOnlyList<string> ForbiddenPhrases);

/// <summary>
///     List request for one agent's golden conversation set. The agent id travels in the route.
/// </summary>
public sealed class ListGoldenConversationsRequest
{
    public Guid AgentDefinitionId { get; init; }
}

/// <summary>
///     Create request for a golden conversation case. The owning agent id travels in the route; the body
///     carries the operator-authored fields. The endpoint serializes <see cref="InputTurns" />/<see cref="Assertion" />
///     to camelCase JSON strings before persisting (the runner parses the same shape). At least one of
///     <see cref="Assertion" />/<see cref="Rubric" /> must be present (enforced by the service).
/// </summary>
public sealed class CreateGoldenConversationRequest
{
    public Guid AgentDefinitionId { get; init; }

    public required string Title { get; init; }

    public required IReadOnlyList<GoldenTurnDto> InputTurns { get; init; }

    public GoldenAssertionDto? Assertion { get; init; }

    public string? Rubric { get; init; }

    public bool Enabled { get; init; } = true;
}

/// <summary>Route request to delete a golden case: both the owning agent id and the golden id travel in the route.</summary>
public sealed class DeleteGoldenConversationRequest
{
    public Guid AgentDefinitionId { get; init; }

    public Guid GoldenConversationId { get; init; }
}

/// <summary>
///     Route request to harvest golden candidates for one agent. The owning agent id travels in the route; the body is
///     empty (the client posts <c>{}</c> — a route-only POST, FastEndpoints 415s a truly empty body).
/// </summary>
public sealed class HarvestGoldenConversationsRequest
{
    public Guid AgentDefinitionId { get; init; }
}

/// <summary>
///     Route request to approve a harvested golden candidate into the active set: both the owning agent id and the golden
///     id travel in the route; the body is empty (the client posts <c>{}</c>).
/// </summary>
public sealed class ApproveGoldenConversationRequest
{
    public Guid AgentDefinitionId { get; init; }

    public Guid GoldenConversationId { get; init; }
}

/// <summary>
///     Wire projection of a stored golden conversation case. <see cref="InputTurns" /> and
///     <see cref="Assertion" /> are deserialized from the persisted JSON strings into the typed DTOs at the boundary so
///     the client never parses raw JSON. The free-text source is encrypted at rest; this projection is the decrypted,
///     typed view.
/// </summary>
public sealed class GoldenConversationResponse
{
    public required Guid Id { get; init; }

    public required Guid AgentDefinitionId { get; init; }

    public required string Title { get; init; }

    public required IReadOnlyList<GoldenTurnDto> InputTurns { get; init; }

    public GoldenAssertionDto? Assertion { get; init; }

    public string? Rubric { get; init; }

    public required bool Enabled { get; init; }

    /// <summary>
    ///     Provenance discriminator — a lowercase literal (<c>"manual"</c>/<c>"harvested"</c>) matching the client's
    ///     config-derived discriminator convention (distinct from PascalCase status enums on the wire).
    /// </summary>
    public required string Source { get; init; }

    /// <summary>The thumbs-up assistant message a harvested case was proposed from; <c>null</c> for manual cases.</summary>
    public Guid? SourceMessageId { get; init; }

    /// <summary>The conversation a harvested case was proposed from; <c>null</c> for manual cases.</summary>
    public Guid? SourceConversationId { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }
}

/// <summary>List response wrapper for an agent's golden cases (mirrors the playbook list <c>{ items }</c> shape).</summary>
public sealed class ListGoldenConversationsResponse
{
    public required IReadOnlyList<GoldenConversationResponse> Items { get; init; }
}

/// <summary>
///     Per-run counts for a golden harvest: thumbs-up sources scanned and how the candidates split across created /
///     already-harvested (duplicate) / skipped. Counts only — the harvested turn/answer text never crosses the wire here
///     (it rides the encrypted golden columns surfaced by the golden list).
/// </summary>
public sealed record GoldenHarvestResponse(
    int ThumbsUpScanned,
    int CreatedCount,
    int DuplicateCount,
    int SkippedCount);

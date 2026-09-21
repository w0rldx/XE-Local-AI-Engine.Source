namespace XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Decrypted, typed projection of a persisted <c>AgentDefinition</c>.
/// </summary>
/// <remarks>
///     <see cref="Instructions" /> and <see cref="Description" /> are returned in plaintext (decrypted on
///     materialization) and the tool lists are materialized from their JSON columns into typed collections; the store
///     converts to and from this shape at the boundary so callers never touch the encrypted byte columns or the raw
///     JSON. <see cref="Source" /> and <see cref="SeedSlug" /> expose the row's provenance, read-side only, for the
///     UI "Seeded" badge and the import idempotency check.
/// </remarks>
public sealed record AgentDefinitionRecord
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required string? Description { get; init; }

    public required string Instructions { get; init; }

    public required string? ModelProfile { get; init; }

    public required string? ReasoningEffort { get; init; }

    public required AgentDefinitionKind Kind { get; init; }

    public required IReadOnlyList<string> AllowedToolNames { get; init; }

    public required IReadOnlyDictionary<string, bool> ToolApprovals { get; init; }

    public required string? OrchestrationTopologyJson { get; init; }

    public required int Version { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }

    public bool PlaybookEnabled { get; init; }

    public AgentDefinitionSource Source { get; init; }

    public string? SeedSlug { get; init; }

    public IReadOnlyList<Guid>? AllowedSkillIds { get; init; }

    public bool DefaultTemporaryChat { get; init; }

    public bool MemoryExtractionEnabled { get; init; } = true;

    public bool DisableBaseScaffold { get; init; }

    public string? GenerationMetadataJson { get; init; }

    public bool DisableToolRelevanceFilter { get; init; }
}

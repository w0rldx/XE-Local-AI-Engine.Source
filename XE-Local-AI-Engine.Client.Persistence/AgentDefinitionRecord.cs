namespace XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Decrypted, typed projection of a persisted <c>AgentDefinition</c>. <see cref="Instructions" /> and
///     <see cref="Description" /> are returned in plaintext (decrypted on materialization); the tool lists are
///     materialized from their JSON columns into typed collections. The store converts to and from this shape at the
///     boundary so callers never touch the encrypted byte columns or the raw JSON. <see cref="Source" /> and
///     <see cref="SeedSlug" /> expose the row's provenance (read-side only) for the UI "Seeded" badge and the import
///     idempotency check.
/// </summary>
public sealed record AgentDefinitionRecord(
    Guid Id,
    string Name,
    string? Description,
    string Instructions,
    string? ModelProfile,
    string? ReasoningEffort,
    AgentDefinitionKind Kind,
    IReadOnlyList<string> AllowedToolNames,
    IReadOnlyDictionary<string, bool> ToolApprovals,
    string? OrchestrationTopologyJson,
    int Version,
    long CreatedAtUtc,
    long UpdatedAtUtc,
    bool PlaybookEnabled = false,
    AgentDefinitionSource Source = AgentDefinitionSource.Manual,
    string? SeedSlug = null,
    IReadOnlyList<Guid>? AllowedSkillIds = null,
    bool DefaultTemporaryChat = false,
    bool MemoryExtractionEnabled = true,
    bool DisableBaseScaffold = false,
    string? GenerationMetadataJson = null,
    bool DisableToolRelevanceFilter = false);

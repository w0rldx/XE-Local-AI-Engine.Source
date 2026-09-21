namespace XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Node-scoped persistence for agent definitions.
/// </summary>
/// <remarks>
///     <c>Instructions</c> and <c>Description</c> are encrypted at rest by the node encryption interceptors; reads
///     return them decrypted on the <see cref="AgentDefinitionRecord" />. The store performs no content validation —
///     that is the application-layer service's responsibility; it owns only id/version/timestamp stamping and the
///     config-affecting version-bump rule.
/// </remarks>
public interface IAgentDefinitionStore
{
    /// <summary>
    ///     Persists a new definition (assigning <c>Id</c>, <c>CreatedAtUtc</c>, <c>UpdatedAtUtc</c> and
    ///     <c>Version = 1</c>) and returns the stored record with free-text columns decrypted.
    /// </summary>
    Task<AgentDefinitionRecord> AddAsync(AgentDefinitionInput input, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Persists a new starter-pack definition exactly like <see cref="AddAsync" /> but stamps
    ///     <c>Source = Seeded</c> and the supplied <paramref name="seedSlug" />. This is the <b>only</b> method that
    ///     mints a seeded row — the operator create/update contract always writes <c>Manual</c> — so provenance stays
    ///     forge-proof.
    /// </summary>
    Task<AgentDefinitionRecord> AddSeededAsync(AgentDefinitionInput input, string seedSlug, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Returns the set of <c>SeedSlug</c> values already present on seeded rows (Ordinal-cased), a cheap
    ///     idempotency check before import. Projects the slug column only — no <c>Instructions</c>/<c>Description</c>
    ///     decryption.
    /// </summary>
    Task<IReadOnlySet<string>> ListSeededSlugsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Returns the seeded definition whose <c>SeedSlug</c> equals <paramref name="seedSlug" /> (Ordinal), from a
    ///     <c>Source = Seeded</c> row, or <c>null</c> when no such row exists.
    /// </summary>
    /// <remarks>
    ///     Used by the chat send/regenerate paths to resolve the node-local "Default Assistant" id (the mode-off
    ///     persona). Free-text columns are decrypted on materialization like any other record read.
    /// </remarks>
    Task<AgentDefinitionRecord?> GetBySeedSlugAsync(string seedSlug, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Applies <paramref name="input" /> to the definition identified by <paramref name="id" />, or returns
    ///     <c>null</c> when no definition has that id.
    /// </summary>
    /// <remarks>
    ///     Stamps <c>UpdatedAtUtc</c> and increments <c>Version</c> only when a config-affecting field changed:
    ///     Instructions, tool lists, approvals, ModelProfile, ReasoningEffort, Kind, topology — never Name/Description
    ///     alone.
    /// </remarks>
    Task<AgentDefinitionRecord?> UpdateAsync(Guid id, AgentDefinitionInput input, CancellationToken cancellationToken = default);

    /// <summary>Removes the definition with <paramref name="id" />. Returns <c>true</c> when a row was deleted.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Returns the record for <paramref name="id" />, or <c>null</c> when no definition has that id.</summary>
    Task<AgentDefinitionRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Returns every registered definition, oldest first.</summary>
    Task<IReadOnlyList<AgentDefinitionRecord>> ListAsync(CancellationToken cancellationToken = default);
}

/// <summary>
///     Mutable fields of an agent definition supplied on create/update.
/// </summary>
/// <remarks>
///     Free text is passed as plaintext strings; the store encodes <see cref="Instructions" /> and
///     <see cref="Description" /> to UTF-8 bytes before the interceptors encrypt them.
///     <see cref="GenerationMetadataJson" /> is set-if-present on update: <c>null</c> leaves the stored AI provenance
///     alone rather than clearing it, so an operator edit that did not echo the block back cannot erase the record of
///     how the definition was drafted.
/// </remarks>
public sealed record AgentDefinitionInput
{
    public required string Name { get; init; }

    public required string? Description { get; init; }

    public required string Instructions { get; init; }

    public required string? ModelProfile { get; init; }

    public required string? ReasoningEffort { get; init; }

    public required AgentDefinitionKind Kind { get; init; }

    public required IReadOnlyList<string> AllowedToolNames { get; init; }

    public required IReadOnlyDictionary<string, bool> ToolApprovals { get; init; }

    public required string? OrchestrationTopologyJson { get; init; }

    public bool PlaybookEnabled { get; init; }

    public IReadOnlyList<Guid>? AllowedSkillIds { get; init; }

    public bool DefaultTemporaryChat { get; init; }

    public bool MemoryExtractionEnabled { get; init; } = true;

    public bool DisableBaseScaffold { get; init; }

    public string? GenerationMetadataJson { get; init; }

    public bool DisableToolRelevanceFilter { get; init; }
}

namespace XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Node-scoped persistence for the agent skill library.
/// </summary>
/// <remarks>
///     <c>Description</c>, <c>Body</c>, the optional frontmatter and every resource payload are encrypted at rest by
///     the node encryption interceptors; reads return them decrypted on the record types below. The store performs no
///     content validation — that is the application-layer service's responsibility; it owns only
///     id/version/timestamp stamping, the content-affecting version-bump rule, and the provenance invariants of
///     docs/wiki/08-data-and-persistence.md ("Agent skill provenance").
/// </remarks>
public interface IAgentSkillStore
{
    /// <summary>
    ///     Persists a new skill (assigning <c>Id</c>, <c>CreatedAtUtc</c>, <c>UpdatedAtUtc</c> and <c>Version = 1</c>)
    ///     and returns the stored record with free-text columns decrypted.
    /// </summary>
    /// <remarks>
    ///     Resources are written separately via <see cref="ReplaceResourcesAsync" />, so the returned record always
    ///     carries an empty resource list.
    /// </remarks>
    Task<AgentSkillRecord> CreateAsync(AgentSkillInput input, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Applies <paramref name="input" /> to the skill identified by <paramref name="id" />, or returns
    ///     <c>null</c> when no skill has that id.
    /// </summary>
    /// <remarks>
    ///     Stamps <c>UpdatedAtUtc</c> and increments <c>Version</c> only when a content-affecting field changed (Name,
    ///     Description, Body or frontmatter — never the <c>Enabled</c> toggle and never provenance alone). Provenance
    ///     is promote-only: an <see cref="AgentSkillOrigin.Imported" /> row stays imported even when the caller passes
    ///     the <see cref="AgentSkillOrigin.Local" /> default, so an edit cannot launder third-party content into
    ///     trusted content. Full rule: docs/wiki/08-data-and-persistence.md ("Agent skill provenance").
    /// </remarks>
    Task<AgentSkillRecord?> UpdateAsync(Guid id, AgentSkillInput input, CancellationToken cancellationToken = default);

    /// <summary>Removes the skill with <paramref name="id" /> and, by cascade, its resources. Returns <c>true</c> when a row was deleted.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Returns the record for <paramref name="id" /> with its resources loaded, or <c>null</c> when no skill has that id.</summary>
    Task<AgentSkillRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Returns every skill in the library, ordered by Name (Ordinal) for a stable list.
    /// </summary>
    /// <remarks>
    ///     Resources are <em>not</em> loaded — the list view does not need to decrypt every bundled file; use
    ///     <see cref="GetByIdAsync" /> or <see cref="ListResourcesAsync" /> for those.
    /// </remarks>
    Task<IReadOnlyList<AgentSkillRecord>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Resolver fast-path: the enabled skills whose <c>Id</c> is in <paramref name="ids" />, each with its
    ///     resources loaded, ordered by Name (Ordinal) for a deterministic resolved set.
    /// </summary>
    /// <remarks>
    ///     The enabled filter runs server-side and the resolver hands the resources to MAF as the skill's level-3
    ///     payload. Ids that are missing or disabled are simply absent from the result; the resolver drops and logs
    ///     them.
    /// </remarks>
    Task<IReadOnlyList<AgentSkillRecord>> ListEnabledByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default);

    /// <summary>Returns the resources of <paramref name="skillId" /> with content decrypted, ordered by Name (Ordinal).</summary>
    Task<IReadOnlyList<AgentSkillResourceRecord>> ListResourcesAsync(Guid skillId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Adds a resource, or replaces the existing one with the same (case-insensitive) name, or returns
    ///     <c>null</c> when no skill has that id.
    /// </summary>
    /// <remarks>
    ///     A replacement is a delete-and-reinsert rather than an in-place edit because the resource name is bound into
    ///     the payload's AAD — the new row is sealed under its own id, so a stale ciphertext can never be read back
    ///     under a name it was not written for. Bumps the owning skill's <c>Version</c>: resources are content the
    ///     model sees, so an edit must invalidate resume.
    /// </remarks>
    Task<AgentSkillResourceRecord?> UpsertResourceAsync(Guid skillId, AgentSkillResourceInput input, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Removes one resource from <paramref name="skillId" />, bumping the skill's <c>Version</c>. Returns
    ///     <c>true</c> when a row was deleted.
    /// </summary>
    Task<bool> DeleteResourceAsync(Guid skillId, Guid resourceId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Replaces the skill's entire resource set in one save, bumping the owning skill's <c>Version</c> once, or
    ///     returns <c>null</c> when no skill has that id.
    /// </summary>
    /// <remarks>
    ///     The import shape: the materialised preview payload is written wholesale, and any file the new payload
    ///     dropped has to disappear with it.
    /// </remarks>
    Task<IReadOnlyList<AgentSkillResourceRecord>?> ReplaceResourcesAsync(Guid skillId,
        IReadOnlyList<AgentSkillResourceInput> resources,
        CancellationToken cancellationToken = default);
}

/// <summary>
///     Decrypted, typed projection of a persisted agent skill.
/// </summary>
/// <remarks>
///     <see cref="Description" />, <see cref="Body" /> and the frontmatter fields are returned in plaintext (decrypted
///     on materialization); the store converts to and from this shape at the boundary so callers never touch the
///     encrypted byte columns or the frontmatter JSON. <see cref="Origin" />, <see cref="SourceUri" />,
///     <see cref="ImportedAtUtc" /> and <see cref="ContentSha256" /> expose the row's provenance for the UI "Imported"
///     badge, the runtime fencing decision and re-import change detection.
/// </remarks>
public sealed class AgentSkillRecord
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required string Description { get; init; }

    public required string Body { get; init; }

    public required bool Enabled { get; init; }

    public required int Version { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }

    public string? License { get; init; }

    public string? Compatibility { get; init; }

    public string? AllowedTools { get; init; }

    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    public AgentSkillOrigin Origin { get; init; }

    public string? SourceUri { get; init; }

    public long? ImportedAtUtc { get; init; }

    public string? ContentSha256 { get; init; }

    public IReadOnlyList<AgentSkillResourceRecord>? Resources { get; init; }

    public string? GenerationMetadataJson { get; init; }
}

/// <summary>
///     Mutable fields of an agent skill supplied on create/update.
/// </summary>
/// <remarks>
///     Free text is passed as plaintext strings; the store encodes <see cref="Description" />, <see cref="Body" /> and
///     the frontmatter to UTF-8 bytes before the interceptors encrypt them. <see cref="AllowedTools" /> stays the
///     spec's space-delimited string rather than a list, because MAF consumes it in that form and a collection would
///     invent a canonical ordering the spec does not have. The <see cref="SourceUri" /> shape check and the
///     set-if-present <see cref="GenerationMetadataJson" /> rule: wiki 08 ("Agent skill provenance").
/// </remarks>
public sealed record AgentSkillInput
{
    public required string Name { get; init; }

    public required string Description { get; init; }

    public required string Body { get; init; }

    public bool Enabled { get; init; } = true;

    public string? License { get; init; }

    public string? Compatibility { get; init; }

    public string? AllowedTools { get; init; }

    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    public AgentSkillOrigin Origin { get; init; }

    public string? SourceUri { get; init; }

    public long? ImportedAtUtc { get; init; }

    public string? ContentSha256 { get; init; }

    public string? GenerationMetadataJson { get; init; }
}

/// <summary>
///     Decrypted projection of one bundled skill file. <see cref="Name" /> is the skill-root-relative path the model
///     looks the file up by; <see cref="SizeBytes" /> is the plaintext UTF-8 length, carried so a list view does not
///     have to measure decrypted content.
/// </summary>
public sealed class AgentSkillResourceRecord
{
    public required Guid Id { get; init; }

    public required Guid SkillId { get; init; }

    public required string Name { get; init; }

    public required string Description { get; init; }

    public required string MediaType { get; init; }

    public required string Content { get; init; }

    public required int SizeBytes { get; init; }
}

/// <summary>
///     Mutable fields of a bundled skill file. The store derives <c>SizeBytes</c> from <see cref="Content" /> — a
///     caller-supplied size could disagree with the payload it labels.
/// </summary>
public sealed class AgentSkillResourceInput
{
    public required string Name { get; init; }

    public required string Description { get; init; }

    public required string MediaType { get; init; }

    public required string Content { get; init; }
}

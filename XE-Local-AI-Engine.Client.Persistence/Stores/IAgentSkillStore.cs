namespace XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Node-scoped persistence for the agent skill library. <c>Description</c>, <c>Body</c>, the optional frontmatter
///     and every resource payload are encrypted at rest by the node encryption interceptors; reads return them
///     decrypted on the record types below. This store performs no content validation — that is the application-layer
///     service's responsibility; it owns only id/version/timestamp stamping, the content-affecting version-bump rule,
///     and the two provenance invariants documented on <see cref="UpdateAsync" /> and <see cref="AgentSkillInput" />.
/// </summary>
public interface IAgentSkillStore
{
    /// <summary>
    ///     Persists a new skill (assigning <c>Id</c>, <c>CreatedAtUtc</c>, <c>UpdatedAtUtc</c> and <c>Version = 1</c>)
    ///     and returns the stored record with free-text columns decrypted. Resources are written separately via
    ///     <see cref="ReplaceResourcesAsync" />, so the returned record always carries an empty resource list.
    /// </summary>
    Task<AgentSkillRecord> CreateAsync(AgentSkillInput input, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Applies <paramref name="input" /> to the skill identified by <paramref name="id" />, stamping
    ///     <c>UpdatedAtUtc</c> and incrementing <c>Version</c> only when a content-affecting field changed (Name,
    ///     Description, Body or frontmatter — never the <c>Enabled</c> toggle and never provenance alone). Returns the
    ///     updated record, or <c>null</c> when no skill has that id.
    ///     <para>
    ///         Provenance is promote-only: an <see cref="AgentSkillOrigin.Imported" /> row stays imported even when the
    ///         caller passes the <see cref="AgentSkillOrigin.Local" /> default. An operator edit that simply forgot to
    ///         echo the provenance back would otherwise launder third-party content into trusted content — stripping
    ///         the untrusted-content fence and re-enabling session-scoped approval for it.
    ///     </para>
    /// </summary>
    Task<AgentSkillRecord?> UpdateAsync(Guid id, AgentSkillInput input, CancellationToken cancellationToken = default);

    /// <summary>Removes the skill with <paramref name="id" /> and, by cascade, its resources. Returns <c>true</c> when a row was deleted.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Returns the record for <paramref name="id" /> with its resources loaded, or <c>null</c> when no skill has that id.</summary>
    Task<AgentSkillRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Returns every skill in the library, ordered by Name (Ordinal) for a stable list. Resources are <em>not</em>
    ///     loaded (the list view does not need to decrypt every bundled file); use <see cref="GetByIdAsync" /> or
    ///     <see cref="ListResourcesAsync" /> for those.
    /// </summary>
    Task<IReadOnlyList<AgentSkillRecord>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Resolver fast-path: the enabled skills whose <c>Id</c> is in <paramref name="ids" />, filtered to
    ///     <c>Enabled == true</c> server-side, each with its resources loaded (the resolver hands them to MAF as the
    ///     skill's level-3 payload). Ids that are missing or disabled are simply absent from the result; the resolver
    ///     drops/logs them. Order is by Name (Ordinal) for a deterministic resolved set.
    /// </summary>
    Task<IReadOnlyList<AgentSkillRecord>> ListEnabledByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default);

    /// <summary>Returns the resources of <paramref name="skillId" /> with content decrypted, ordered by Name (Ordinal).</summary>
    Task<IReadOnlyList<AgentSkillResourceRecord>> ListResourcesAsync(Guid skillId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Adds a resource, or replaces the existing one with the same (case-insensitive) name. A replacement is a
    ///     delete-and-reinsert rather than an in-place edit because the resource name is bound into the payload's AAD —
    ///     the new row is sealed under its own id, so a stale ciphertext can never be read back under a name it was not
    ///     written for. Bumps the owning skill's <c>Version</c> (resources are content the model sees, so an edit must
    ///     invalidate resume). Returns the stored resource, or <c>null</c> when no skill has that id.
    /// </summary>
    Task<AgentSkillResourceRecord?> UpsertResourceAsync(Guid skillId, AgentSkillResourceInput input, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Removes one resource from <paramref name="skillId" />, bumping the skill's <c>Version</c>. Returns
    ///     <c>true</c> when a row was deleted.
    /// </summary>
    Task<bool> DeleteResourceAsync(Guid skillId, Guid resourceId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Replaces the skill's entire resource set in one save — the import shape, where the materialised preview
    ///     payload is written wholesale and any file the new payload dropped has to disappear with it. Bumps the
    ///     owning skill's <c>Version</c> once. Returns the stored resources, or <c>null</c> when no skill has that id.
    /// </summary>
    Task<IReadOnlyList<AgentSkillResourceRecord>?> ReplaceResourcesAsync(Guid skillId,
        IReadOnlyList<AgentSkillResourceInput> resources,
        CancellationToken cancellationToken = default);
}

/// <summary>
///     Decrypted, typed projection of a persisted agent skill. <see cref="Description" />, <see cref="Body" /> and the
///     frontmatter fields are returned in plaintext (decrypted on materialization); the store converts to and from this
///     shape at the boundary so callers never touch the encrypted byte columns or the frontmatter JSON.
///     <see cref="Origin" />, <see cref="SourceUri" />, <see cref="ImportedAtUtc" /> and <see cref="ContentSha256" />
///     expose the row's provenance for the UI "Imported" badge, the runtime fencing decision and re-import change
///     detection.
/// </summary>
public sealed record AgentSkillRecord(
    Guid Id,
    string Name,
    string Description,
    string Body,
    bool Enabled,
    int Version,
    long CreatedAtUtc,
    long UpdatedAtUtc,
    string? License = null,
    string? Compatibility = null,
    string? AllowedTools = null,
    IReadOnlyDictionary<string, string>? Metadata = null,
    AgentSkillOrigin Origin = AgentSkillOrigin.Local,
    string? SourceUri = null,
    long? ImportedAtUtc = null,
    string? ContentSha256 = null,
    IReadOnlyList<AgentSkillResourceRecord>? Resources = null);

/// <summary>
///     Mutable fields of an agent skill supplied on create/update. Free text is passed as plaintext strings; the store
///     encodes <see cref="Description" />, <see cref="Body" /> and the frontmatter to UTF-8 bytes before the
///     interceptors encrypt them.
///     <para>
///         <see cref="AllowedTools" /> is the spec's space-delimited string, kept verbatim rather than split into a
///         list — MAF consumes it in that form and round-tripping through a collection would only invent a canonical
///         ordering the spec does not have.
///     </para>
///     <para>
///         <see cref="SourceUri" /> is shape-checked at the store boundary: the literal <c>upload</c>, the literal
///         <c>generated</c> (AI-drafted content) or <c>github:owner/repo</c>. An uploaded or drafted skill contributes
///         its <em>kind</em> only — the operator's filename, or the model that drafted it, must not become the one
///         unencrypted free-text string in this table.
///     </para>
/// </summary>
public sealed record AgentSkillInput(
    string Name,
    string Description,
    string Body,
    bool Enabled = true,
    string? License = null,
    string? Compatibility = null,
    string? AllowedTools = null,
    IReadOnlyDictionary<string, string>? Metadata = null,
    AgentSkillOrigin Origin = AgentSkillOrigin.Local,
    string? SourceUri = null,
    long? ImportedAtUtc = null,
    string? ContentSha256 = null);

/// <summary>
///     Decrypted projection of one bundled skill file. <see cref="Name" /> is the skill-root-relative path the model
///     looks the file up by; <see cref="SizeBytes" /> is the plaintext UTF-8 length, carried so a list view does not
///     have to measure decrypted content.
/// </summary>
public sealed record AgentSkillResourceRecord(
    Guid Id,
    Guid SkillId,
    string Name,
    string Description,
    string MediaType,
    string Content,
    int SizeBytes);

/// <summary>
///     Mutable fields of a bundled skill file. The store derives <c>SizeBytes</c> from <see cref="Content" /> — a
///     caller-supplied size could disagree with the payload it labels.
/// </summary>
public sealed record AgentSkillResourceInput(
    string Name,
    string Description,
    string MediaType,
    string Content);

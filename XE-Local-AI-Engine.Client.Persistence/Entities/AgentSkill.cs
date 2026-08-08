namespace XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed record class AgentSkill
{
    public Guid Id { get; set; }

    /// <summary>
    ///     MAF skill name (identifier/routing surface). Plaintext for list/lookup; NOCASE-unique. Not part of the
    ///     encrypted surface — mirrors <see cref="AgentDefinition.Name" />.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    ///     MAF skill description as UTF-8 bytes — the short summary the model sees to decide whether to load the body.
    ///     Plaintext while tracked in memory; encrypted at rest by <see cref="NodeEncryptionSaveChangesInterceptor" /> and
    ///     decrypted by <see cref="NodeEncryptionMaterializationInterceptor" /> using AAD column name <c>description</c>.
    ///     Required.
    /// </summary>
    public byte[] Description { get; set; } = [];

    /// <summary>
    ///     SKILL.md markdown body as UTF-8 bytes — the on-demand-loaded instructions (progressive disclosure). Plaintext
    ///     while tracked in memory; encrypted at rest by <see cref="NodeEncryptionSaveChangesInterceptor" /> and decrypted
    ///     by <see cref="NodeEncryptionMaterializationInterceptor" /> using AAD column name <c>body</c>. Required.
    /// </summary>
    public byte[] Body { get; set; } = [];

    /// <summary>
    ///     Library-wide on/off switch. Plaintext (structural). Disabled skills are never resolved even when still
    ///     assigned to an agent. Default <c>true</c>; toggling it does NOT bump <see cref="Version" />.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    ///     Optional spec frontmatter as a single UTF-8 JSON object — <c>{license, compatibility, allowedTools,
    ///     metadata}</c>. One column rather than four because the fields are optional, sparsely used, and
    ///     <c>metadata</c> is arbitrary operator- or third-party-supplied content. Plaintext while tracked in memory;
    ///     encrypted at rest by <see cref="NodeEncryptionSaveChangesInterceptor" /> and decrypted by
    ///     <see cref="NodeEncryptionMaterializationInterceptor" /> using AAD column name <c>frontmatter_json</c>. Null
    ///     when every field is absent.
    /// </summary>
    public byte[]? FrontmatterJson { get; set; }

    /// <summary>
    ///     Backing int for <see cref="AgentSkillOrigin" />; provenance of the row. Plaintext (structural — the UI badge
    ///     reads it and the resolver branches on it to fence imported content). Default <c>0</c> (Local), backfilled
    ///     <c>0</c> for every pre-import row.
    /// </summary>
    public int Origin { get; set; }

    /// <summary>
    ///     Where an <see cref="AgentSkillOrigin.Imported" /> row came from, or <c>null</c> for a local skill.
    ///     Deliberately plaintext: provenance is shown in the UI and has to be greppable in logs. For that reason it
    ///     carries the <em>kind only</em> (<c>upload</c>) for uploaded archives — an operator-chosen filename must not
    ///     become the one unencrypted free-text string in a table where everything else is AEAD-sealed. A GitHub source
    ///     keeps its full <c>github:owner/repo</c> value, which is already public. Shape is enforced at the store
    ///     boundary.
    /// </summary>
    public string? SourceUri { get; set; }

    /// <summary>Epoch-millisecond stamp of the import that created or last replaced this row; <c>null</c> for a local skill.</summary>
    public long? ImportedAtUtc { get; set; }

    /// <summary>
    ///     SHA-256 over the canonical import payload, used for change detection when re-importing the same source.
    ///     Explicitly <em>not</em> a trust signal — it says the bytes are the same ones we saw before, not that they are
    ///     safe. <c>null</c> for a local skill.
    /// </summary>
    public string? ContentSha256 { get; set; }

    /// <summary>
    ///     Bumped on a content-affecting edit (Name/Description/Body/frontmatter, and any resource add, edit or
    ///     removal); drives the runtime config hash so editing a skill invalidates resume. <see cref="Enabled" />
    ///     toggles do not bump it (membership in the resolved set already covers that in the hash), and neither does
    ///     provenance. Default <c>1</c>.
    /// </summary>
    public int Version { get; set; }

    public long CreatedAtUtc { get; set; }

    public long UpdatedAtUtc { get; set; }
}

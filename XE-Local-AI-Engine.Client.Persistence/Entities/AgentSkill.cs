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
    ///     Bumped on a content-affecting edit (Name/Description/Body); drives the runtime config hash so editing a skill
    ///     invalidates resume. <see cref="Enabled" /> toggles do not bump it (membership in the resolved set already
    ///     covers that in the hash). Default <c>1</c>.
    /// </summary>
    public int Version { get; set; }

    public long CreatedAtUtc { get; set; }

    public long UpdatedAtUtc { get; set; }
}

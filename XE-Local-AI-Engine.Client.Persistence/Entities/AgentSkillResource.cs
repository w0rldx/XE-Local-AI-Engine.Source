namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     One bundled file of an agent skill — the level-3 payload the model fetches by name after loading the SKILL.md
///     body. Owned by exactly one <see cref="AgentSkill" /> and deleted with it (cascade).
/// </summary>
internal sealed record class AgentSkillResource
{
    public Guid Id { get; set; }

    /// <summary>Owning skill. Plaintext (structural), FK with cascade delete, and part of the content AAD — see <see cref="Content" />.</summary>
    public Guid SkillId { get; set; }

    /// <summary>
    ///     Lookup key the model uses, stored as the skill-root-relative path (<c>references/FAQ.md</c>) because MAF
    ///     tells the model to pass the name exactly as listed. Plaintext; NOCASE-unique per skill. Immutable for the
    ///     life of a row: it is bound into the content AAD, so an edit is a delete-and-reinsert (which re-seals the
    ///     payload under the new name) rather than an in-place rename.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Short label shown to the model alongside the name so it can decide whether to fetch this file. Plaintext.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Media type inferred from the file extension at import time. Plaintext (structural).</summary>
    public string MediaType { get; set; } = string.Empty;

    /// <summary>
    ///     The file's UTF-8 text. Plaintext while tracked in memory; encrypted at rest by
    ///     <see cref="NodeEncryptionSaveChangesInterceptor" /> and decrypted by
    ///     <see cref="NodeEncryptionMaterializationInterceptor" />. The AAD binds <see cref="SkillId" /> and
    ///     <see cref="Name" /> in addition to this row's own id — every other encrypted column in this schema binds only
    ///     the row id, which would be wrong here for the same reason it would be wrong for the inbound-MCP key hash:
    ///     the threat is a database <em>writer</em>, not a reader. Without the skill id in the AAD, anyone who could
    ///     edit the file could re-parent a resource row onto a different skill and have its content injected into
    ///     another agent's context without ever forging a ciphertext or a tag.
    /// </summary>
    public byte[] Content { get; set; } = [];

    /// <summary>Plaintext UTF-8 byte length, kept alongside the ciphertext so list views and caps do not have to decrypt. Plaintext.</summary>
    public int SizeBytes { get; set; }

    /// <summary>
    ///     AAD column name for <see cref="Content" />, carrying the resource name so it is authenticated alongside the
    ///     skill id and the row id. Shared by both encryption interceptors so the seal and the open can never drift.
    /// </summary>
    public static string ContentColumnName(string name)
    {
        return $"skill_resource_content:{name}";
    }
}

namespace XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed record class AgentDefinition
{
    public Guid Id { get; set; }

    /// <summary>Display label. Plaintext for list/search; not part of the encrypted surface.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    ///     Optional free-text description as UTF-8 bytes. Plaintext while tracked in memory; encrypted at rest by
    ///     <see cref="NodeEncryptionSaveChangesInterceptor" /> and decrypted by
    ///     <see cref="NodeEncryptionMaterializationInterceptor" /> using AAD column name <c>description</c>.
    /// </summary>
    public byte[]? Description { get; set; }

    /// <summary>
    ///     System prompt as UTF-8 bytes. Plaintext while tracked in memory; encrypted at rest by
    ///     <see cref="NodeEncryptionSaveChangesInterceptor" /> and decrypted by
    ///     <see cref="NodeEncryptionMaterializationInterceptor" /> using AAD column name <c>instructions</c>. Required.
    /// </summary>
    public byte[] Instructions { get; set; } = [];

    /// <summary>Pinned model profile, or <c>null</c> to use the node default. Plaintext (structural).</summary>
    public string? ModelProfile { get; set; }

    /// <summary>Normalized reasoning effort (low|none|medium|high), or <c>null</c>. Plaintext (structural).</summary>
    public string? ReasoningEffort { get; set; }

    /// <summary>Backing int for <see cref="AgentDefinitionKind" />. Plaintext (structural).</summary>
    public int Kind { get; set; }

    /// <summary>JSON array of allowed tool names. Plaintext (structural).</summary>
    public string AllowedToolNamesJson { get; set; } = "[]";

    /// <summary>
    ///     JSON array of assigned skill Guids (the per-agent skill picklist). Plaintext (structural — ids only). Changing
    ///     it is config-affecting (bumps <see cref="Version" />), same class as <see cref="AllowedToolNamesJson" />.
    /// </summary>
    public string AllowedSkillIdsJson { get; set; } = "[]";

    /// <summary>JSON map of tool name to required-approval flag. Plaintext (structural).</summary>
    public string ToolApprovalsJson { get; set; } = "{}";

    /// <summary>Orchestration topology JSON. Persisted but ignored by the current single-agent runtime (orchestration execution is not wired yet). Plaintext.</summary>
    public string? OrchestrationTopologyJson { get; set; }

    /// <summary>
    ///     Whether this agent's enabled playbook actions are folded into its resolved system prompt. Plaintext
    ///     (structural). Gates injection only — it is NOT a config-affecting field for the agent's own version bump,
    ///     because the injected playbook content drives the runtime package config hash directly.
    /// </summary>
    public bool PlaybookEnabled { get; set; }

    /// <summary>
    ///     Backing int for <see cref="AgentDefinitionSource" />; provenance of the row. Default <c>0</c> (Manual).
    ///     Plaintext (structural) — like <see cref="Name" />, not part of the encrypted surface and never set by the
    ///     operator create/update contract (only the import path stamps Seeded).
    /// </summary>
    public int Source { get; set; }

    /// <summary>
    ///     Stable starter-pack import key (the catalog slug) for a <see cref="AgentDefinitionSource.Seeded" /> row, or
    ///     <c>null</c> for a manual row. Plaintext (structural); a filtered unique index enforces one row per slug so a
    ///     re-import never duplicates a seeded persona.
    /// </summary>
    public string? SeedSlug { get; set; }

    /// <summary>
    ///     Bumped on each config-affecting update; feeds the runtime package version and config hash so a definition
    ///     edit invalidates resume the same way a server-side version bump does.
    /// </summary>
    public int Version { get; set; }

    public long CreatedAtUtc { get; set; }

    public long UpdatedAtUtc { get; set; }
}

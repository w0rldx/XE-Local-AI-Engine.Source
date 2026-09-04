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

    /// <summary>
    ///     AI-generation provenance for a drafted definition as a UTF-8 JSON object, or <c>null</c> for a row with no
    ///     AI provenance. Plaintext while tracked in memory; encrypted at rest by
    ///     <see cref="NodeEncryptionSaveChangesInterceptor" /> and decrypted by
    ///     <see cref="NodeEncryptionMaterializationInterceptor" /> using AAD column name
    ///     <c>generation_metadata_json</c>. Informational only: on a single-operator node the operator supplies most of
    ///     it, so it records what a draft claimed, not a tamper-proof attestation.
    /// </summary>
    public byte[]? GenerationMetadataJson { get; set; }

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

    /// <summary>Orchestration topology JSON for a <c>Kind=Orchestrator</c> definition, compiled by the application-layer orchestration resolver into the loopback runtime graph. Plaintext.</summary>
    public string? OrchestrationTopologyJson { get; set; }

    /// <summary>
    ///     Whether this agent's enabled playbook actions are folded into its resolved system prompt. Plaintext
    ///     (structural). Gates injection only — it is NOT a config-affecting field for the agent's own version bump,
    ///     because the injected playbook content drives the runtime package config hash directly.
    /// </summary>
    public bool PlaybookEnabled { get; set; }

    /// <summary>
    ///     Opt-out for the versioned, app-owned base instruction scaffold (identity/grounding/tool/output discipline)
    ///     normally prepended ahead of this definition's <see cref="Instructions" /> when composing the resolved
    ///     prompt. Plaintext (structural). Default <c>false</c> (scaffold ON). Non-config-affecting for this
    ///     definition's own <see cref="Version" /> bump — like <see cref="PlaybookEnabled" /> — because toggling it
    ///     changes the resolved prompt directly, which already drives the runtime package config hash.
    /// </summary>
    public bool DisableBaseScaffold { get; set; }

    /// <summary>
    ///     Per-agent opt-out from the send-time tool-relevance filter: with this set, every offered tool is put in front
    ///     of the model on every round even when the node has the filter enabled and the agent carries more tools than
    ///     the threshold. Plaintext (structural). Default <c>false</c> (follow the node setting). Non-config-affecting:
    ///     the filter narrows only the array handed to the provider, never the offer, the resolved prompt or the runtime
    ///     package's config hash, so toggling it can never invalidate a resume.
    /// </summary>
    public bool DisableToolRelevanceFilter { get; set; }

    /// <summary>
    ///     Per-agent default for the temporary-chat (memory write-only-suppression) flag a new conversation inherits.
    ///     Plaintext (structural). Non-config-affecting (exactly like <see cref="PlaybookEnabled" />): it gates post-run
    ///     memory extraction only and must NOT enter the runtime package config hash or bump the agent's own version.
    /// </summary>
    public bool DefaultTemporaryChat { get; set; }

    /// <summary>
    ///     Whether this agent mines its completed runs into NEW candidate memories (post-run extraction). Default
    ///     <c>true</c> so opting into <see cref="PlaybookEnabled" /> preserves today's learn-from-runs behaviour. When
    ///     <c>false</c> the agent is RETRIEVAL-ONLY: it still injects its existing enabled memory (gated on
    ///     <see cref="PlaybookEnabled" />) but its runs never trigger the extraction round-trip. Plaintext (structural).
    ///     Non-config-affecting (exactly like <see cref="PlaybookEnabled" />): it gates extraction only and must NOT enter
    ///     the runtime package config hash or bump the agent's own version.
    /// </summary>
    public bool MemoryExtractionEnabled { get; set; } = true;

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

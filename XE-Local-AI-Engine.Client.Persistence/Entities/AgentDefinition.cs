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

    /// <summary>JSON map of tool name to required-approval flag. Plaintext (structural).</summary>
    public string ToolApprovalsJson { get; set; } = "{}";

    /// <summary>Orchestration topology JSON. Persisted but ignored by the P3 runtime (P5 executes it). Plaintext.</summary>
    public string? OrchestrationTopologyJson { get; set; }

    /// <summary>
    ///     Bumped on each config-affecting update; feeds the runtime package version and config hash so a definition
    ///     edit invalidates resume the same way a server-side version bump does.
    /// </summary>
    public int Version { get; set; }

    public long CreatedAtUtc { get; set; }

    public long UpdatedAtUtc { get; set; }
}

namespace XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed record class PlaybookAction
{
    public Guid Id { get; set; }

    /// <summary>Owning agent definition. Real FK to <c>agent_definitions.id</c> with cascade delete; indexed. Plaintext (structural).</summary>
    public Guid AgentDefinitionId { get; set; }

    /// <summary>Backing int for <see cref="PlaybookActionState" />. Plaintext (structural).</summary>
    public int State { get; set; }

    /// <summary>Backing int for <see cref="PlaybookActionSource" /> (provenance). Plaintext (structural).</summary>
    public int Source { get; set; }

    /// <summary>
    ///     Optional retrieval/trigger hint as UTF-8 bytes. Plaintext while tracked in memory; encrypted at rest by
    ///     <see cref="NodeEncryptionSaveChangesInterceptor" /> and decrypted by
    ///     <see cref="NodeEncryptionMaterializationInterceptor" /> using AAD column name <c>trigger_condition</c>.
    ///     Advisory/display in P1 (not injected); drives retrieval in later phases.
    /// </summary>
    public byte[]? TriggerCondition { get; set; }

    /// <summary>
    ///     Instruction text injected into the agent's system prompt, as UTF-8 bytes. Plaintext while tracked in memory;
    ///     encrypted at rest by <see cref="NodeEncryptionSaveChangesInterceptor" /> and decrypted by
    ///     <see cref="NodeEncryptionMaterializationInterceptor" /> using AAD column name <c>behavior</c>. Required.
    /// </summary>
    public byte[] Behavior { get; set; } = [];

    /// <summary>Optional topic/tool/intent tag (structural, filterable). Plaintext.</summary>
    public string? Scope { get; set; }

    /// <summary>Injection order; enabled actions inject ascending by this value. Plaintext (structural).</summary>
    public int Priority { get; set; }

    /// <summary>Per-action audit/dedup counter; bumped on a config-affecting edit (Behavior/Priority/State).</summary>
    public int Version { get; set; }

    public long CreatedAtUtc { get; set; }

    public long UpdatedAtUtc { get; set; }
}

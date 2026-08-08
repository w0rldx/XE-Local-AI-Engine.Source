namespace XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed record class GoldenConversation
{
    public Guid Id { get; set; }

    /// <summary>Owning agent definition. Real FK to <c>agent_definitions.id</c> with cascade delete; indexed. Plaintext (structural).</summary>
    public Guid AgentDefinitionId { get; set; }

    /// <summary>Operator label for the golden case (plaintext, like <see cref="PlaybookAction.Scope" />).</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    ///     Conversation up to the eval point as UTF-8 bytes (JSON <c>[{role,text}, …]</c>). Plaintext while tracked in
    ///     memory; encrypted at rest by <see cref="NodeEncryptionSaveChangesInterceptor" /> and decrypted by
    ///     <see cref="NodeEncryptionMaterializationInterceptor" /> using AAD column name <c>input_turns</c>. Required.
    /// </summary>
    public byte[] InputTurns { get; set; } = [];

    /// <summary>
    ///     Optional deterministic assertion as UTF-8 bytes (JSON <c>{ requiredPhrases[], forbiddenPhrases[] }</c>).
    ///     Plaintext while tracked in memory; encrypted at rest by <see cref="NodeEncryptionSaveChangesInterceptor" /> and
    ///     decrypted by <see cref="NodeEncryptionMaterializationInterceptor" /> using AAD column name <c>assertion</c>.
    /// </summary>
    public byte[]? Assertion { get; set; }

    /// <summary>
    ///     Optional judge rubric text as UTF-8 bytes. Plaintext while tracked in memory; encrypted at rest by
    ///     <see cref="NodeEncryptionSaveChangesInterceptor" /> and decrypted by
    ///     <see cref="NodeEncryptionMaterializationInterceptor" /> using AAD column name <c>rubric</c>.
    /// </summary>
    public byte[]? Rubric { get; set; }

    /// <summary>Operator can park a case without deleting it. Plaintext (structural).</summary>
    public bool Enabled { get; set; }

    /// <summary>Provenance: hand-authored (<see cref="GoldenConversationSource.Manual" />) or harvested from a thumbs-up turn. Plaintext (structural).</summary>
    public GoldenConversationSource Source { get; set; }

    /// <summary>The thumbs-up assistant message a harvested case was proposed from — provenance + dedup key. Null for manual cases. Plaintext Guid (not sensitive).</summary>
    public Guid? SourceMessageId { get; set; }

    /// <summary>The conversation a harvested case was proposed from — provenance. Null for manual cases. Plaintext Guid (not sensitive).</summary>
    public Guid? SourceConversationId { get; set; }

    public long CreatedAtUtc { get; set; }

    public long UpdatedAtUtc { get; set; }
}

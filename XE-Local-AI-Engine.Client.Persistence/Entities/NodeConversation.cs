namespace XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed record class NodeConversation
{
    public Guid ConversationId { get; set; }

    /// <summary>
    ///     Conversation title as UTF-8 bytes (verbatim first 120 chars of first user message). Plaintext while tracked
    ///     in memory; encrypted at rest by <see cref="NodeEncryptionSaveChangesInterceptor" /> and decrypted by
    ///     <see cref="NodeEncryptionMaterializationInterceptor" /> using AAD column name <c>title</c>. Optional.
    /// </summary>
    public byte[]? Title { get; set; }

    public string? UserId { get; set; }

    public long CreatedAtUtc { get; set; }

    public long LastSeenUtc { get; set; }

    public bool Purged { get; set; }

    public bool IsPinned { get; set; }

    public bool Archived { get; set; }

    public string Origin { get; set; } = NodeChatOrigin.Local;

    /// <summary>
    ///     What created this conversation, and therefore whether the chat list shows it: one of
    ///     <see cref="NodeConversationKind" />. Plaintext (structural).
    /// </summary>
    /// <remarks>
    ///     Defaults to <c>chat</c>, and the migration backfills <c>work-session</c> onto every conversation an
    ///     <see cref="AgentWorkSession" /> owns. The two conversation LIST queries filter on it; by-id reads, purge
    ///     and retention stay unfiltered on purpose.
    /// </remarks>
    public string Kind { get; set; } = NodeConversationKind.Chat;

    /// <summary>
    ///     When this conversation was created by branching another, the source conversation id. Null for
    ///     conversations that were not branched. Provenance only — branched rows are independent
    ///     (Origin=Local) and never sync back.
    /// </summary>
    public Guid? BranchOfConversationId { get; set; }

    /// <summary>
    ///     JSON metadata map of variantGroupId-&gt;selectedMessageId capturing which sibling variant is selected on
    ///     each branched turn.
    /// </summary>
    /// <remarks>
    ///     Topology lives on the messages (parent/variant-group); this column is selection metadata only, so it is
    ///     additive, nullable, and E2E-safe (never required to reconstruct the conversation tree).
    /// </remarks>
    public string? SelectedPathJson { get; set; }

    /// <summary>
    ///     The node-local agent definition this conversation is bound to, or null for the implicit default persona.
    /// </summary>
    /// <remarks>
    ///     A loose nullable Guid with no enforced FK (mirrors <see cref="BranchOfConversationId" />): a binding that
    ///     points at a deleted definition is treated as null by the resolver rather than failing the read.
    /// </remarks>
    public Guid? AgentDefinitionId { get; set; }

    /// <summary>
    ///     Temporary-chat (adaptive-memory write-only-suppression) flag. Plaintext (a bool); default/backfill false.
    /// </summary>
    /// <remarks>
    ///     When true, the post-run memory-extraction seam skips this conversation entirely; it does NOT affect
    ///     retrieval/injection (a temp chat still reads existing memory) or chat persistence (the conversation is
    ///     still saved). The conversation read/write paths use raw ADO SQL, so this property exists mainly so the EF
    ///     model snapshot and EnsureCreated() track the column; the raw column-lists in
    ///     NodeChatConversationCommands/NodeChatPersistenceSql are the actual reader/writer.
    /// </remarks>
    public bool MemoryExcluded { get; set; }

    /// <summary>
    ///     Derived, non-destructive compaction synopsis: a local-model summary of the older turns, sent in their place
    ///     so a long conversation keeps its gist within the context window. Null until the user compacts.
    /// </summary>
    /// <remarks>
    ///     The originals are not deleted and remain in <see cref="Messages" />. UTF-8 bytes encrypted at rest under
    ///     AAD column name <c>compaction_summary</c> — same posture as <see cref="Title" />. See
    ///     <see cref="CompactionSummaryCoversToSequence" /> for which messages it folds in.
    /// </remarks>
    public byte[]? CompactionSummary { get; set; }

    /// <summary>
    ///     The highest message <c>Sequence</c> folded into <see cref="CompactionSummary" />. On a send, messages at or
    ///     below this sequence are replaced by the synopsis and only newer turns are sent verbatim. Plaintext; null when
    ///     no summary exists.
    /// </summary>
    public int? CompactionSummaryCoversToSequence { get; set; }

    /// <summary>When <see cref="CompactionSummary" /> was last (re)generated. Plaintext Unix-ms; null when no summary exists.</summary>
    public long? CompactionSummaryUpdatedAtUtc { get; set; }

    public List<NodeMessage> Messages { get; } = [];

    public List<NodeToolEvent> ToolEvents { get; } = [];
}

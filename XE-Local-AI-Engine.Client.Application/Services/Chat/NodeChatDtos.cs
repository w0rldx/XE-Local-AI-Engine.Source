namespace XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     Transport DTO for node chat conversation summary data.
/// </summary>
public sealed class NodeChatConversationSummaryDto
{
    public required Guid ConversationId { get; init; }

    public required string? Title { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long LastSeenUtc { get; init; }

    public required string? LastMessagePreview { get; init; }

    public required string? LastMessageStatus { get; init; }

    public required bool Purged { get; init; }

    public string Origin { get; init; } = NodeChatOriginValues.Local;

    public bool IsPinned { get; init; }

    public bool Archived { get; init; }
}

/// <summary>
///     Transport DTO for node chat conversation data.
/// </summary>
public sealed record NodeChatConversationDto
{
    public required Guid ConversationId { get; init; }

    public required string? Title { get; init; }

    public required string? UserId { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long LastSeenUtc { get; init; }

    public required bool Purged { get; init; }

    public required IReadOnlyList<NodeChatPersistedMessageDto> Messages { get; init; }

    public string Origin { get; init; } = NodeChatOriginValues.Local;

    public bool IsPinned { get; init; }

    public bool Archived { get; init; }

    public Guid? BranchOfConversationId { get; init; }

    public IReadOnlyDictionary<Guid, Guid>? SelectedPath { get; init; }

    public Guid? AgentDefinitionId { get; init; }

    public bool MemoryExcluded { get; init; }

    // The decrypted compaction synopsis plus the highest ANCHOR sequence it folds in, never a chosen sibling's own
    // sequence. Null until compacted; the send path substitutes it for the covered messages, which stay in Messages.
    public string? CompactionSummary { get; init; }

    public int? CompactionSummaryCoversToSequence { get; init; }

    public long? CompactionSummaryUpdatedAtUtc { get; init; }
}

/// <summary>
///     One ordered part of an assistant turn: a reasoning segment, a tool call collapsed by
///     <see cref="ToolCallId" /> and carrying its result, or an interleaved text segment.
/// </summary>
/// <remarks>
///     Parts persist in the <c>metadata_json</c> column alongside the flattened <c>Reasoning</c> so reload restores
///     the exact interleave. The raw-ADO path writes that column through
///     <c>NodeChatDbContext.EncryptMessageMetadata</c>, so parts are encrypted at rest under the same per-record AAD
///     as the reasoning already in the blob. Optional fields are null for the kinds that do not use them.
/// </remarks>
public sealed record NodeChatMessagePart(
    string Kind,
    int Sequence,
    string? Text = null,
    string? ToolCallId = null,
    string? Name = null,
    string? State = null,
    string? Args = null,
    string? Result = null,
    bool? RequiresApproval = null);

/// <summary>
///     One knowledge-base chunk that grounded a plain-chat assistant turn, captured at retrieval time from the fused
///     hits fenced into the turn's context.
/// </summary>
/// <remarks>
///     It persists on the assistant message's <c>metadata_json</c> blob, additively and with no migration, so the
///     client renders a "Sources" strip on the live refetch and a later reload. It carries only the NON-SENSITIVE
///     provenance <see cref="KnowledgeSearchHit" /> already discloses: the title and section are derived from
///     heading and storage paths, never the encrypted file name, and no chunk body text rides here.
/// </remarks>
public sealed record NodeChatMessageSource(
    Guid DocumentId,
    Guid ChunkId,
    string Title,
    string? Section,
    double Score);

/// <summary>
///     Transport DTO for node chat persisted message data, whose <c>Parts</c> is the ordered interleave; it is null
///     for a legacy message, and the client then synthesizes one Thoughts block from <c>Reasoning</c>.
/// </summary>
public sealed record NodeChatPersistedMessageDto : ISelectedPathMessage
{
    public required Guid MessageId { get; init; }

    public required Guid ConversationId { get; init; }

    public required Guid? RequestId { get; init; }

    public required int Sequence { get; init; }

    // ISelectedPathMessage is satisfied by the existing MessageId/Sequence/VariantGroupId/CreatedAtUtc members,
    // so the SelectedPathResolver can collapse these messages to the selected variant path with no projection.
    public required string Role { get; init; }

    public required string Content { get; init; }

    public required string? Reasoning { get; init; }

    public required string Status { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }

    public required string? Model { get; init; }

    public required string? Error { get; init; }

    public required string? MetadataJson { get; init; }

    public int? InputCount { get; init; }

    public int? OutputCount { get; init; }

    public int? TotalCount { get; init; }

    public int? ReasoningCount { get; init; }

    public string Origin { get; init; } = NodeChatOriginValues.Local;

    public Guid? ParentMessageId { get; init; }

    public Guid? VariantGroupId { get; init; }

    public string? FeedbackRating { get; init; }

    public string? FeedbackComment { get; init; }

    public IReadOnlyList<NodeChatMessagePart>? Parts { get; init; }

    // Per-response agent attribution surfaced from the metadata blob, with no DB column: the agent's id plus its
    // display name at send time, which survives a later rename or delete. Both null for a legacy turn.
    public Guid? AgentDefinitionId { get; init; }

    public string? AgentName { get; init; }

    // The reasoning effort actually used to generate this assistant turn, surfaced from the metadata blob (no DB
    // column). Null for legacy turns persisted before this field existed and for user messages.
    public string? ReasoningEffort { get; init; }

    // Whole-turn wall-clock generation duration in milliseconds, surfaced from the metadata blob, driving the
    // optional tokens-per-second attribution. Null for a legacy turn, the platform path and user messages.
    public long? GenerationDurationMs { get; init; }

    // Knowledge-base sources that grounded this plain-chat assistant turn, surfaced from the metadata
    // blob (no DB column). Null/empty for legacy turns, turns that did not use the knowledge base, and user messages.
    public IReadOnlyList<NodeChatMessageSource>? Sources { get; init; }
}

/// <summary>
///     Transport DTO for node chat cancel result data.
/// </summary>
public sealed class NodeChatCancelResultDto
{
    public required NodeChatMessageCorrelation Correlation { get; init; }

    public required string Status { get; init; }

    public required bool Cancelled { get; init; }
}

/// <summary>
///     Transport DTO for node chat delete result data.
/// </summary>
public sealed class NodeChatDeleteResultDto
{
    public required Guid ConversationId { get; init; }

    public required bool CancelRequested { get; init; }

    public required bool Purged { get; init; }
}

/// <summary>
///     Transport DTO for node chat branch result data.
/// </summary>
public sealed class NodeChatBranchResultDto
{
    public required Guid SourceConversationId { get; init; }

    public required Guid BranchedConversationId { get; init; }

    public required int CopiedMessageCount { get; init; }
}

/// <summary>
///     Transport DTO for node chat message variant data.
/// </summary>
public sealed class NodeChatMessageVariantDto
{
    public required Guid VariantGroupId { get; init; }

    public required Guid OriginalMessageId { get; init; }

    public required NodeChatPersistedMessageDto Variant { get; init; }
}

/// <summary>
///     Transport DTO for node chat message feedback data.
/// </summary>
public sealed class NodeChatMessageFeedbackDto
{
    public required Guid MessageId { get; init; }

    public required Guid ConversationId { get; init; }

    public required string Rating { get; init; }

    public required string? Comment { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }
}

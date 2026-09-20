namespace XE_Local_AI_Engine.Client.Services.Chat;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Represents node chat origin values.
/// </summary>
public static class NodeChatOriginValues
{
    public const string Local = "Local";
    public const string Remote = "Remote";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Local,
        Remote
    };
}

/// <summary>
///     Represents node chat message status values.
/// </summary>
public static class NodeChatMessageStatusValues
{
    public const string Pending = "pending";
    public const string Queued = "queued";
    public const string Streaming = "streaming";
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";
    public const string Failed = "failed";
    public const string Interrupted = "interrupted";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Pending,
        Queued,
        Streaming,
        Completed,
        Cancelled,
        Failed,
        Interrupted
    };

    /// <summary>
    ///     The non-terminal statuses a message may still be cancelled from, so a late cancel on a finished row is
    ///     rejected without a rewrite. A conversation delete cancels its active messages on the same filter.
    /// </summary>
    public static readonly IReadOnlySet<string> Cancellable = new HashSet<string>(StringComparer.Ordinal)
    {
        Pending,
        Queued,
        Streaming
    };
}

/// <summary>
///     The request to create a conversation, optionally at a caller-minted id.
/// </summary>
/// <remarks>
///     <see cref="Kind" /> is the <c>conversations.kind</c> discriminator and defaults to <c>chat</c>, so every
///     ordinary caller keeps its behaviour. <see cref="ConversationId" /> lets the integration accept path commit its
///     durable rows first and create the owned conversation afterwards, at the id the session row already carries;
///     <c>null</c> keeps the mint-your-own behaviour. The column is the primary key, so a colliding id is a
///     <c>SqliteException</c> and a caller bug, exactly as on the mint path.
/// </remarks>
public sealed record NodeChatCreateConversationRequest
{
    public required string? Title { get; init; }

    public required string? UserId { get; init; }

    public required long CreatedAtUtc { get; init; }

    public string Origin { get; init; } = NodeChatOriginValues.Local;

    public Guid? AgentDefinitionId { get; init; }

    public string Kind { get; init; } = NodeConversationKind.Chat;

    public Guid? ConversationId { get; init; }
}

public sealed record NodeChatEnsureConversationRequest
{
    public required Guid ConversationId { get; init; }

    public required string? Title { get; init; }

    public required string? UserId { get; init; }

    public required long CreatedAtUtc { get; init; }

    public string Origin { get; init; } = NodeChatOriginValues.Local;
}

public sealed class NodeChatListConversationsRequest
{
    public bool IncludeArchived { get; init; }

    public int? Limit { get; init; }
}

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

public sealed class NodeChatPersistUserMessageRequest
{
    public required Guid ConversationId { get; init; }

    public required Guid MessageId { get; init; }

    public required string Content { get; init; }

    public required long CreatedAtUtc { get; init; }

    public string? MetadataJson { get; init; }

    public string Origin { get; init; } = NodeChatOriginValues.Local;
}

public sealed class NodeChatCreateAssistantPlaceholderRequest
{
    public required Guid ConversationId { get; init; }

    public required Guid MessageId { get; init; }

    public required Guid RequestId { get; init; }

    public required long CreatedAtUtc { get; init; }

    public string? Model { get; init; }

    public string? MetadataJson { get; init; }

    public string Origin { get; init; } = NodeChatOriginValues.Local;

    // Per-response agent attribution stamped at send time into the metadata blob, with no DB column, so the pending
    // placeholder already carries the name. Null on cold and fallback paths, where the client labels it itself.
    public Guid? AgentDefinitionId { get; init; }

    public string? AgentName { get; init; }

    // The reasoning effort that will drive this turn's generation, stamped at send time into the metadata blob (no DB
    // column) so the persisted assistant turn records it. Null when no effort was selected.
    public string? ReasoningEffort { get; init; }
}

public sealed record NodeChatMessageCorrelation
{
    public required Guid ConversationId { get; init; }

    public required Guid MessageId { get; init; }

    public required Guid RequestId { get; init; }
}

public sealed class NodeChatPartialFlushRequest
{
    public required NodeChatMessageCorrelation Correlation { get; init; }

    public required string Content { get; init; }

    public required string? Reasoning { get; init; }

    public required long UpdatedAtUtc { get; init; }

    public bool ReplaceContent { get; init; } = true;
}

public sealed class NodeChatTerminalizeMessageRequest
{
    public required NodeChatMessageCorrelation Correlation { get; init; }

    public required string Status { get; init; }

    public required long UpdatedAtUtc { get; init; }

    public string? Content { get; init; }

    public string? Reasoning { get; init; }

    public string? Error { get; init; }

    public string? Model { get; init; }

    public int? InputCount { get; init; }

    public int? OutputCount { get; init; }

    public int? TotalCount { get; init; }

    public int? ReasoningCount { get; init; }

    // Ordered interleave assembled from the run's reasoning segments + tool lifecycle. Null leaves any existing parts
    // untouched; an empty list is a meaningful "no parts" (e.g. a plain-text turn) and overwrites.
    public IReadOnlyList<NodeChatMessagePart>? Parts { get; init; }

    // Whole-turn wall-clock generation duration in milliseconds (drives the optional tokens-per-second attribution).
    // Trailing optional so legacy callers and the platform path leave it null. Null preserves any existing value.
    public long? GenerationDurationMs { get; init; }

    // Durable run-envelope payload: when supplied, the terminalize command writes the content-free envelope row in the
    // SAME transaction as the terminal message row. Its status and agent id come from the winning row, not from here.
    public AgentRunEnvelopeMetadata? Envelope { get; init; }

    // Knowledge-base sources that grounded this turn, which the plain-chat send path passes so they land on the
    // terminal row's metadata_json. Null preserves any existing persisted sources, as Parts does.
    public IReadOnlyList<NodeChatMessageSource>? Sources { get; init; }
}

/// <summary>
///     Non-derived fields of a durable run envelope, supplied by the pump to the terminalize persistence command.
/// </summary>
/// <remarks>
///     It is bounded and content-free: correlation and timing counters and a trace id only, NEVER a prompt, model
///     output or tool argument, and <see cref="FailureCategory" /> is a category enum name. Terminal status, success,
///     tokens, model and the bound agent id come from the persisted row inside the terminalize transaction.
/// </remarks>
public sealed class AgentRunEnvelopeMetadata
{
    public required Guid? InvocationId { get; init; }

    public required long DurationMs { get; init; }

    public string? FailureCategory { get; init; }

    public int? ContentChunkCount { get; init; }

    public int? ReasoningChunkCount { get; init; }

    public string? TraceId { get; init; }

    public long? StartedAtUtc { get; init; }

    // The runtime provider that served the turn, a non-sensitive category label resolved at terminalization from the
    // run's model id. It defaults to 'unknown', so a path that attributes none still writes a valid label.
    public string Provider { get; init; } = AgentUsageProviders.Unknown;

    // Tool-schema token estimate for the turn, cumulative over its provider rounds and for the largest single round.
    // Counts only, never a tool name, and null on the thin path that has no invocation state to read them from.
    public long? ToolSchemaTokens { get; init; }

    public int? MaxToolSchemaTokens { get; init; }

    // What reasoning effort `auto` resolved to for the turn: the tier label and the authored effort. Trailing optional
    // for the same reason as the pair above, and null on every turn that authored a concrete effort.
    public string? DispatchedTier { get; init; }

    public string? AuthoredEffort { get; init; }

    // How much of DurationMs went into making a LOCAL runtime ready rather than generating. The whole-turn clock
    // starts before the warm, so recording the two separately is what makes a cold arm comparable with a warm one.
    public long? ModelReadinessMs { get; init; }

    // The turn's token usage SUMMED over its provider rounds: what the turn COST, where the message row's tokens are
    // the LAST round's context OCCUPANCY. The envelope writes these when present, else the message's.
    public int? TurnInputTokens { get; init; }

    public int? TurnOutputTokens { get; init; }

    public int? TurnTotalTokens { get; init; }

    public int? TurnReasoningTokens { get; init; }
}

public sealed class NodeChatCancelRequest
{
    public required NodeChatMessageCorrelation Correlation { get; init; }

    public required long CancelledAtUtc { get; init; }
}

public sealed class NodeChatDeleteConversationRequest
{
    public required Guid ConversationId { get; init; }

    public required long DeletedAtUtc { get; init; }

    public bool PurgeImmediately { get; init; }
}

public sealed class NodeChatRenameConversationRequest
{
    public required Guid ConversationId { get; init; }

    public required string? Title { get; init; }

    public required long UpdatedAtUtc { get; init; }
}

public sealed class NodeChatSetConversationPinnedRequest
{
    public required Guid ConversationId { get; init; }

    public required bool IsPinned { get; init; }

    public required long UpdatedAtUtc { get; init; }
}

public sealed class NodeChatSetConversationArchivedRequest
{
    public required Guid ConversationId { get; init; }

    public required bool Archived { get; init; }

    public required long UpdatedAtUtc { get; init; }
}

/// <summary>
///     Sets the conversation's temporary-chat (<c>memory_excluded</c>) flag — the per-conversation override of the
///     bound agent's default (adaptive memory, write-only extraction suppression).
/// </summary>
public sealed class NodeChatSetConversationMemoryExcludedRequest
{
    public required Guid ConversationId { get; init; }

    public required bool MemoryExcluded { get; init; }

    public required long UpdatedAtUtc { get; init; }
}

/// <summary>
///     Writes (or clears) the conversation's non-destructive compaction synopsis. The summary is encrypted at rest; the
///     covered sequence and timestamp are plaintext. A null <see cref="Summary" /> clears the synopsis (all three
///     columns reset to NULL).
/// </summary>
public sealed class NodeChatSetCompactionSummaryRequest
{
    public required Guid ConversationId { get; init; }

    public required string? Summary { get; init; }

    public required int? CoversToSequence { get; init; }

    public required long UpdatedAtUtc { get; init; }
}

/// <summary>
///     The kind of an ordered assistant message part, from which the interleaved render region is reconstructed on
///     reload so the live and reloaded views match; <c>text</c> covers the rarer mid-turn narration case.
/// </summary>
public static class NodeChatMessagePartKinds
{
    public const string Reasoning = "reasoning";
    public const string Tool = "tool";
    public const string Text = "text";

    /// <summary>
    ///     A non-fatal turn notice (model substitution, tool disabled, history truncated). Reuses the generic
    ///     <see cref="NodeChatMessagePart.Text" /> for the sanitized message and <see cref="NodeChatMessagePart.Name" />
    ///     for the <c>TurnNoticeKind</c> enum name, rather than adding dedicated fields.
    /// </summary>
    public const string Notice = "notice";
}

/// <summary>
///     Represents the lifecycle state of a tool part. Mirrors the client tool-call state union; persisted tool parts
///     carry the terminal state (<see cref="Received" /> or <see cref="Failed" />) once the tool has completed.
/// </summary>
public static class NodeChatToolPartStates
{
    public const string Requesting = "requesting";
    public const string Waiting = "waiting";
    public const string Received = "received";
    public const string Failed = "failed";
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
///     Represents node chat feedback rating values.
/// </summary>
public static class NodeChatFeedbackRatingValues
{
    public const string Up = "up";
    public const string Down = "down";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Up,
        Down
    };
}

/// <summary>
///     Conversation branch: clones the source conversation's messages up to and including <see cref="MessageId" />
///     into a NEW conversation. The new conversation is Origin=Local and records
///     <c>branch_of_conversation_id</c> = source for provenance.
/// </summary>
public sealed class NodeChatBranchConversationRequest
{
    public required Guid ConversationId { get; init; }

    public required Guid MessageId { get; init; }

    public required long CreatedAtUtc { get; init; }

    /// <summary>
    ///     Optional caller-supplied selected-revision map, mirroring the persisted selected-path shape, pinning which
    ///     upstream variant each group contributes to the branched linear thread.
    /// </summary>
    /// <remarks>
    ///     It makes the branch match the path the user was viewing rather than always copying the newest revision.
    ///     Null or empty falls every group back to its newest eligible revision, and the branch-point turn always
    ///     contributes exactly <see cref="MessageId" />, overriding any entry for its group.
    /// </remarks>
    public IReadOnlyDictionary<Guid, Guid>? SelectedRevisions { get; init; }
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
///     Thrown when a message-correlated write names no persisted message: the pair does not exist, or the request id
///     does not match the row it addresses.
/// </summary>
/// <remarks>
///     Endpoints that answer "not found" for a bad correlation catch THIS rather than
///     <see cref="InvalidOperationException" />, so an unrelated fault under the same call cannot present as a 404.
///     It still derives from that type, which the pump and the other correlated writers already read as "this write
///     cannot proceed". It is about the correlation, not about a message a person named, which
///     <c>NodeChatMessageNotFoundException</c> covers.
/// </remarks>
public sealed class NodeChatMessageCorrelationNotFoundException : InvalidOperationException
{
    public NodeChatMessageCorrelationNotFoundException(string message) : base(message)
    {
    }
}

/// <summary>
///     Thrown when a branch request's selected-revision entry fails integrity validation: its message is not in the
///     conversation, or it is keyed under a group it does not belong to.
/// </summary>
/// <remarks>
///     The branch endpoint maps it to HTTP 400. It fails closed, rejecting the branch rather than silently falling
///     back to a default revision.
/// </remarks>
public sealed class NodeChatInvalidBranchSelectionException : InvalidOperationException
{
    public const string Code = "invalid-branch-selection";

    public NodeChatInvalidBranchSelectionException(Guid conversationId, Guid variantGroupId, Guid messageId) : base($"Branch selection for conversation {conversationId} referenced message {messageId} which is not a valid member of variant group {variantGroupId}.")
    {
        ConversationId = conversationId;
        VariantGroupId = variantGroupId;
        MessageId = messageId;
    }

    public Guid ConversationId { get; }

    public Guid VariantGroupId { get; }

    public Guid MessageId { get; }
}

/// <summary>
///     Assistant revision: records a regenerated turn as a SIBLING VARIANT, never an in-place overwrite.
/// </summary>
/// <remarks>
///     Every variant of one logical turn shares a <c>variant_group_id</c>, and <see cref="ParentMessageId" /> is the
///     user turn they answer. A null <see cref="VariantGroupId" /> mints a fresh group and back-stamps the original.
/// </remarks>
public sealed class NodeChatCreateMessageVariantRequest
{
    public required Guid ConversationId { get; init; }

    public required Guid OriginalMessageId { get; init; }

    public required Guid NewMessageId { get; init; }

    public required Guid RequestId { get; init; }

    public required long CreatedAtUtc { get; init; }

    public string? Model { get; init; }

    public string? MetadataJson { get; init; }

    // Per-response agent attribution for the variant, stamped at mint time on the same metadata-blob path as the send
    // placeholder. It is re-resolved, so a rename is picked up and a deleted agent falls back to the stored name.
    public Guid? AgentDefinitionId { get; init; }

    public string? AgentName { get; init; }

    // The reasoning effort used to generate this regenerated variant, stamped at mint time into the metadata blob (no
    // DB column). Null when no effort was selected.
    public string? ReasoningEffort { get; init; }
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
///     Persists the conversation's selected-path map {variantGroupId-&gt;selectedMessageId} (which sibling variant is
///     chosen on each branched turn). Selection metadata only — the conversation tree topology lives on the messages.
/// </summary>
public sealed class NodeChatSetSelectedPathRequest
{
    public required Guid ConversationId { get; init; }

    public required IReadOnlyDictionary<Guid, Guid>? SelectedPath { get; init; }

    public required long UpdatedAtUtc { get; init; }
}

public sealed class NodeChatSetMessageFeedbackRequest
{
    public required Guid ConversationId { get; init; }

    public required Guid MessageId { get; init; }

    public required string Rating { get; init; }

    public required string? Comment { get; init; }

    public required long UpdatedAtUtc { get; init; }
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

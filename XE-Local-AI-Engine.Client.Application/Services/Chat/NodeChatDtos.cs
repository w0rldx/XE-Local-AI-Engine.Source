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
    ///     The non-terminal statuses from which a message may still be cancelled. Once a row reaches any terminal status
    ///     (completed / cancelled / failed / interrupted) a late cancel is rejected without a rewrite, so it can never
    ///     overwrite a message that has already finished. Mirrors the status filter a conversation delete uses when it
    ///     cancels its still-active messages.
    /// </summary>
    public static readonly IReadOnlySet<string> Cancellable = new HashSet<string>(StringComparer.Ordinal)
    {
        Pending,
        Queued,
        Streaming
    };
}

/// <summary>
///     <see cref="Kind" /> is the <c>conversations.kind</c> discriminator (<see cref="NodeConversationKind" />);
///     it defaults to <c>chat</c>, so every ordinary caller keeps its behaviour. <see cref="ConversationId" />
///     lets a caller create the conversation at an id it minted earlier — the integration accept path commits its
///     durable rows first and creates the owned conversation afterwards, at the id the session row already carries.
///     Passing <c>null</c> keeps the mint-your-own behaviour. The column is the primary key, so a colliding id is a
///     <c>SqliteException</c> and a caller bug, exactly as it would be on the mint path.
/// </summary>
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

    // Non-destructive compaction synopsis (decrypted) plus the highest message ANCHOR sequence it folds in — a variant
    // group's earliest member sequence, not the chosen sibling's own (SelectedPathResolver.CreateAnchorResolver). Null
    // until the conversation has been compacted. The send path replaces messages anchored at/below
    // CompactionSummaryCoversToSequence with CompactionSummary; the originals stay in Messages.
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

    // Per-response agent attribution stamped at send time. Threaded into the metadata blob (no DB column) so the
    // pending placeholder already carries the agent name; null on cold/fallback paths (client renders the localized
    // "Default Assistant" label).
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

    // Durable run-envelope payload. When supplied, the terminalize persistence command writes the
    // content-free envelope row in the SAME transaction as the terminal message row, so the two commit or roll back
    // together (no swallowed best-effort write). Null on paths that write no envelope. The terminal status/success and
    // the bound agent id are derived from the winning persisted message row inside the transaction, so they are NOT
    // carried here.
    public AgentRunEnvelopeMetadata? Envelope { get; init; }

    // Knowledge-base sources that grounded this turn. Null leaves any existing persisted sources
    // untouched (mirrors the Parts null-preserves semantics); the plain-chat send path passes the retrieved sources
    // here so they land on the terminal row's metadata_json. Trailing optional so all other callers stay unchanged.
    public IReadOnlyList<NodeChatMessageSource>? Sources { get; init; }
}

/// <summary>
///     Non-derived fields of a durable run envelope supplied by the pump to the terminalize persistence command. Bounded
///     and content-free: correlation/timing counters and a trace id only — NEVER prompt, model output, or tool arguments.
///     <see cref="FailureCategory" /> is a category enum name only. Terminal status, success, tokens, model, and the
///     bound agent id are derived from the persisted message row inside the terminalize transaction and are not repeated here.
/// </summary>
public sealed class AgentRunEnvelopeMetadata
{
    public required Guid? InvocationId { get; init; }

    public required long DurationMs { get; init; }

    public string? FailureCategory { get; init; }

    public int? ContentChunkCount { get; init; }

    public int? ReasoningChunkCount { get; init; }

    public string? TraceId { get; init; }

    public long? StartedAtUtc { get; init; }

    // Fine-grained runtime provider that served the turn (a non-sensitive category label; see AgentUsageProviders).
    // Resolved at terminalization from the run's model id; defaults to 'unknown' so a caller that does not attribute a
    // provider (the interrupted/thin-cancel path) writes a valid label and the column default is honoured.
    public string Provider { get; init; } = AgentUsageProviders.Unknown;

    // Tool-schema token estimate for the turn: cumulative over its provider rounds, and the largest single round.
    // Counts only — never a tool name. Trailing optional so every existing construction site compiles unchanged, and
    // null on the thin interrupted/cancel path that has no invocation state to read them from.
    public long? ToolSchemaTokens { get; init; }

    public int? MaxToolSchemaTokens { get; init; }

    // What reasoning effort `auto` resolved to for the turn: the tier label and the authored effort. Trailing optional
    // for the same reason as the pair above, and null on every turn that authored a concrete effort.
    public string? DispatchedTier { get; init; }

    public string? AuthoredEffort { get; init; }

    // How much of DurationMs went into making a LOCAL runtime ready (llama-server launch + model load) rather than
    // generating. The whole-turn clock starts before the warm, so a cold turn's duration is dominated by it; recording
    // the two separately is what makes a cold arm comparable with a warm one. Null when no local warm happened
    // (Ollama, a remote provider) and on the thin interrupted/cancel path. A duration only — no model identity.
    public long? ModelReadinessMs { get; init; }

    // The turn's token usage SUMMED over its provider rounds — what the turn COST. The message row's tokens are the
    // LAST round's instead (a round's prompt is the whole conversation so far, so the final round is what the model's
    // context HELD, which is the occupancy the chat meter reads); summing there overstated it threefold. When these are
    // present the envelope row's prompt/completion/reasoning/total columns are written from them, otherwise from the
    // message's tokens — so the restart-recovery backfill and the platform path, which supply none, keep today's values.
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
///     Represents the kind of an ordered assistant message part. The interleaved render region (reasoning segments
///     and tool cards, ordered by <see cref="NodeChatMessagePart.Sequence" />) is reconstructed from these parts on
///     reload so the live and reloaded views are identical. <c>text</c> covers the rarer mid-turn narration case.
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
///     One ordered part of an assistant turn: a reasoning segment, a tool call (collapsed requested-&gt;completed by
///     <see cref="ToolCallId" />, including its result), or an interleaved text segment. Persisted in the
///     <c>metadata_json</c> column alongside the flattened <c>Reasoning</c> so reload restores the exact interleave.
///     That column is written via raw ADO.NET (<c>Encoding.UTF8.GetBytes</c>), the same plaintext-at-rest posture as
///     the pre-existing reasoning/model/token fields on this path (single-user device; documented in
///     <c>NodeChatPersistenceServiceTests</c>). Parts add no new exposure beyond what reasoning already carries.
///     Optional fields are null for the kinds that do not use them (e.g. a reasoning part has only
///     <see cref="Text" />).
/// </summary>
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
///     One knowledge-base chunk that grounded a plain-chat assistant turn. Captured at retrieval time
///     from the fused hybrid-search hits that were fenced into the turn's context, and persisted on the assistant
///     message's <c>metadata_json</c> blob (additive — no DB migration) so the client can render a "Sources" strip on
///     both the live post-stream refetch and a later reload. Carries only the NON-SENSITIVE provenance the knowledge
///     tool already discloses (<see cref="KnowledgeSearchHit" />): the title/section are derived from heading/storage
///     paths, never the encrypted original file name, and no chunk body text rides here.
/// </summary>
public sealed record NodeChatMessageSource(
    Guid DocumentId,
    Guid ChunkId,
    string Title,
    string? Section,
    double Score);

/// <summary>
///     Transport DTO for node chat persisted message data. <c>Parts</c> is the ordered interleave (reasoning segments
///     plus tool cards); it is null for legacy messages persisted before parts existed, in which case the client
///     synthesizes a single Thoughts block from <c>Reasoning</c>.
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

    // Per-response agent attribution snapshot, surfaced from the metadata blob (no DB column). AgentDefinitionId is the
    // provenance of the agent that produced the turn; AgentName is its display-name snapshot at send time (survives a
    // later rename/delete). Both are null for legacy turns persisted before agent mode existed.
    public Guid? AgentDefinitionId { get; init; }

    public string? AgentName { get; init; }

    // The reasoning effort actually used to generate this assistant turn, surfaced from the metadata blob (no DB
    // column). Null for legacy turns persisted before this field existed and for user messages.
    public string? ReasoningEffort { get; init; }

    // Whole-turn wall-clock generation duration in milliseconds, surfaced from the metadata blob (no DB column).
    // Null for legacy turns persisted before this field existed, the platform path, and user messages. Drives the
    // optional tokens-per-second attribution alongside OutputCount.
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
    ///     Optional caller-supplied selected-revision map (<c>variantGroupId -&gt; selectedMessageId</c>), mirroring the
    ///     persisted selected-path shape. It pins which upstream variant each group contributes to the branched linear
    ///     thread, so the branch matches the path the user was actually viewing rather than always copying the newest
    ///     revision. Null or empty ⇒ every group falls back to its newest eligible revision (the legacy behavior). The
    ///     branch-point turn always contributes exactly <see cref="MessageId" />, overriding any entry for its group.
    /// </summary>
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
///     Thrown when a message-correlated write names no persisted message: the conversation/message pair does not
///     exist, or the request id does not match the row it addresses. Endpoints that answer "not found" for a bad
///     correlation catch THIS rather than <see cref="InvalidOperationException" />, so an unrelated fault raised
///     anywhere under the same call cannot present itself as a 404.
///     <para>
///         It still derives from <see cref="InvalidOperationException" /> because that is the type the streaming pump
///         and the other correlated writers already treat as "this write cannot proceed". It is distinct from
///         <c>NodeChatMessageNotFoundException</c>, which the regeneration service raises and <c>LocalChatHub</c>
///         translates for the client — this one is about the correlation, not about a message a person named.
///     </para>
/// </summary>
public sealed class NodeChatMessageCorrelationNotFoundException : InvalidOperationException
{
    public NodeChatMessageCorrelationNotFoundException(string message) : base(message)
    {
    }
}

/// <summary>
///     Thrown when a branch request carries a selected-revision entry that fails integrity validation — the
///     referenced message is not part of the conversation, or it is keyed under a variant group it does not belong
///     to. The branch endpoint maps this to HTTP 400. Fail-closed: the branch is rejected rather than silently
///     falling back to a default revision.
/// </summary>
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
///     Assistant revision: records a regenerated assistant turn as a SIBLING VARIANT (never an in-place
///     overwrite). All variants of one logical turn share a <c>variant_group_id</c>; <see cref="ParentMessageId" />
///     is the user turn the variants answer. When <see cref="VariantGroupId" /> is null a fresh group is minted and
///     the originating message is back-stamped into it.
/// </summary>
public sealed class NodeChatCreateMessageVariantRequest
{
    public required Guid ConversationId { get; init; }

    public required Guid OriginalMessageId { get; init; }

    public required Guid NewMessageId { get; init; }

    public required Guid RequestId { get; init; }

    public required long CreatedAtUtc { get; init; }

    public string? Model { get; init; }

    public string? MetadataJson { get; init; }

    // Per-response agent attribution for the regenerated variant, stamped at mint time (re-resolved → picks up a
    // rename; falls back to the original's stored name when the agent was deleted). Same metadata-blob path as the
    // send placeholder; trailing optional so existing callers are unaffected.
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

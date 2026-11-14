namespace XE_Local_AI_Engine.Client.Services.Chat;

using System.Text.Json.Serialization;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Services.Knowledge;

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

/// <summary>One conversation a content search matched, with a short excerpt around the first hit.</summary>
public sealed class NodeChatConversationSearchHitDto
{
    public required Guid ConversationId { get; init; }

    public required string? Title { get; init; }

    /// <summary>Whitespace-collapsed text around the first content hit; null when the terms matched only the title.</summary>
    public required string? Snippet { get; init; }

    /// <summary><see cref="NodeChatSearchMatchValues.Title" /> or <see cref="NodeChatSearchMatchValues.Content" />.</summary>
    public required string MatchedIn { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long LastSeenUtc { get; init; }

    public required string Origin { get; init; }

    public required bool IsPinned { get; init; }

    public required bool Archived { get; init; }
}

/// <summary>The matches, newest first, and whether the start-up backfill is still filling the index.</summary>
public sealed class NodeChatConversationSearchResultDto
{
    public required IReadOnlyList<NodeChatConversationSearchHitDto> Items { get; init; }

    /// <summary>True until every conversation stored before this process started has been indexed.</summary>
    public required bool Indexing { get; init; }
}

/// <summary>Where a search hit was found first.</summary>
public static class NodeChatSearchMatchValues
{
    public const string Title = "title";
    public const string Content = "content";
}

/// <summary>A conversation's plain mode and its decrypted custom system prompt, read without its messages.</summary>
public sealed record NodeChatConversationPlainMode
{
    public required bool IsPlain { get; init; }

    public required string? PlainSystemPrompt { get; init; }
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

    /// <summary>Plain conversation: no agent, persona, BaseScaffold, tools, knowledge base, skills or memory extraction.</summary>
    public bool IsPlain { get; init; }

    /// <summary>The decrypted custom system prompt of a plain conversation; null sends no system prompt.</summary>
    public string? PlainSystemPrompt { get; init; }

    /// <summary>The conversation's own sampling settings; null means the client's defaults apply. Stored, never applied here.</summary>
    public SamplingOptions? SamplingOptions { get; init; }

    // The decrypted compaction synopsis plus the highest ANCHOR sequence it folds in, never a chosen sibling's own
    // sequence. Null until compacted; the send path substitutes it for the covered messages, which stay in Messages.
    public string? CompactionSummary { get; init; }

    public int? CompactionSummaryCoversToSequence { get; init; }

    public long? CompactionSummaryUpdatedAtUtc { get; init; }

    // The decrypted distilled-state JSON plus its ANCHOR watermark, same space as the synopsis. Null until distilled.
    public string? ConversationState { get; init; }

    public int? ConversationStateCoversToSequence { get; init; }

    // Also stamped when a path change or variant mint clears the state, so a stale distillation write can be rejected.
    public long? ConversationStateUpdatedAtUtc { get; init; }
}

/// <summary>
///     One ordered part of an assistant turn: a reasoning segment, a tool call collapsed by
///     <see cref="ToolCallId" /> and carrying its result, or an interleaved text segment.
/// </summary>
/// <remarks>
///     Parts persist in the <c>metadata_json</c> column alongside the flattened <c>Reasoning</c> so reload restores
///     the exact interleave. The raw-ADO path writes that column through
///     <c>NodeChatDbContext.EncryptMessageMetadata</c>, so parts are encrypted at rest under the same per-record AAD
///     as the reasoning already in the blob. Optional fields are null for the kinds that do not use them. A tool part
///     whose sandbox command streamed output keeps its redacted tail; the three output keys are omitted when null.
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
    bool? RequiresApproval = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<NodeChatToolOutputLine>? OutputTail = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? OutputSkippedLines = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LastCommandLabel = null);

/// <summary>One redacted line of a tool's sandbox command output, live on the stream and persisted in a tool part's tail.</summary>
public sealed class NodeChatToolOutputLine
{
    /// <summary>One of <see cref="NodeChatToolOutputStreams" />.</summary>
    public string Stream { get; init; } = NodeChatToolOutputStreams.Stdout;

    /// <summary>The ANSI-stripped, credential- and workspace-redacted, length-capped text.</summary>
    public string Text { get; init; } = string.Empty;
}

/// <summary>The closed vocabulary of <see cref="NodeChatToolOutputLine.Stream" />.</summary>
public static class NodeChatToolOutputStreams
{
    public const string Stdout = "stdout";
    public const string Stderr = "stderr";
}

/// <summary>
///     One knowledge-base chunk that grounded a plain-chat assistant turn, captured at retrieval time from the fused
///     hits fenced into the turn's context.
/// </summary>
/// <remarks>
///     <see cref="ScoreKind" /> names the scale of <see cref="Score" />; it is null on sources persisted before it existed.
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
    double Score,
    KnowledgeScoreKind? ScoreKind = null);

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

    // Content-free context-window snapshot of this assistant turn's last provider round, surfaced from the metadata
    // blob (no DB column). Null for legacy turns, user messages and turns that never reached a provider round.
    public NodeChatContextWindowDto? ContextWindow { get; init; }

    // Time to first token and prompt/generation throughput of this assistant turn, surfaced from the metadata blob (no DB
    // column). Null for legacy turns, user messages and providers that report no timings.
    public NodeChatThroughputDto? Throughput { get; init; }

    // The category of a failed assistant turn, surfaced from the metadata blob (no DB column). Null for a legacy turn,
    // a turn that did not fail, and a stored name this build does not know.
    public FailureCategory? FailureCategory { get; init; }
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

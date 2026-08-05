namespace XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1;

using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Services.Chat;

public sealed class CreateNodeChatConversationRequest
{
    public string? Title { get; init; }

    public string? UserId { get; init; }

    /// <summary>
    ///     Optional binding to a node-local agent definition. When set, the new conversation runs the bound
    ///     definition's persona/tools/model; null (the default) keeps the implicit default chat persona.
    /// </summary>
    public Guid? AgentDefinitionId { get; init; }
}

public sealed class ListNodeChatConversationsRequest
{
    public bool IncludeArchived { get; init; }

    public int? Limit { get; init; }
}

public sealed class ListNodeChatConversationsResponse
{
    public required IReadOnlyList<NodeChatConversationSummaryResponse> Items { get; init; }
}

public sealed class GetNodeChatConversationRequest
{
    public Guid ConversationId { get; init; }
}

public sealed class DeleteNodeChatConversationRequest
{
    public Guid ConversationId { get; init; }

    public bool PurgeImmediately { get; init; }
}

public sealed class RenameNodeChatConversationRequest
{
    public Guid ConversationId { get; init; }

    public string? Title { get; init; }
}

public sealed class PinNodeChatConversationRequest
{
    public Guid ConversationId { get; init; }

    public bool IsPinned { get; init; }
}

public sealed class ArchiveNodeChatConversationRequest
{
    public Guid ConversationId { get; init; }

    public bool Archived { get; init; }
}

/// <summary>
///     Sets the per-conversation temporary-chat (<c>memory_excluded</c>) override (adaptive memory). The conversation id
///     travels in the route; the body carries the new flag.
/// </summary>
public sealed class SetNodeChatConversationMemoryExcludedRequest
{
    public Guid ConversationId { get; init; }

    public bool MemoryExcluded { get; init; }
}

/// <summary>
///     Triggers non-destructive compaction of a conversation: its older turns are summarized (local model) into an
///     encrypted synopsis sent in their place on later turns. The conversation id travels in the route; there is no body.
/// </summary>
public sealed class CompactNodeChatConversationRequest
{
    public Guid ConversationId { get; init; }

    /// <summary>
    ///     The model the user is chatting with, forwarded so compaction summarizes with the user's own selection when it
    ///     is an installed local chat model (a cloud/unknown selection falls back to a node-local model). Optional; a
    ///     body-less request (null) uses the node's local default. Travels in the body; the id travels in the route.
    /// </summary>
    public string? Model { get; init; }
}

/// <summary>
///     Result of a compaction attempt. <see cref="Outcome" /> is one of the
///     <c>ConversationCompactionOutcome</c> names ("Compacted", "NothingToCompact", "NoLocalModel",
///     "SummarizerReturnedNothing", "ConversationNotFound"); the remaining fields are populated only when a synopsis was
///     produced.
/// </summary>
public sealed class CompactNodeChatConversationResponse
{
    public required string Outcome { get; init; }

    public string? Summary { get; init; }

    public int? CoversToSequence { get; init; }

    public int MessagesFolded { get; init; }

    public long? UpdatedAtUtc { get; init; }

    /// <summary>The local model that produced the synopsis.</summary>
    public string? ModelUsed { get; init; }

    /// <summary>True when the user's selected model was a cloud/unknown model and summarization ran on a node-local model instead.</summary>
    public bool UsedFallbackModel { get; init; }
}

public sealed class CancelNodeChatMessageRequest
{
    public Guid ConversationId { get; init; }

    public Guid MessageId { get; init; }

    public Guid RequestId { get; init; }
}

/// <summary>
///     The operator's decision on a pending tool-approval request. <see cref="RequestId" /> is the approval
///     request id the <c>approval-requested</c> stream event carried (a plain string, not a Guid — the runner mints it
///     as an opaque key), which the runner uses to release the exact waiting tool call.
/// </summary>
public sealed class ResolveToolApprovalRequest
{
    public required string RequestId { get; init; }

    public required bool Approved { get; init; }

    /// <summary>
    ///     How long an APPROVE lasts. Additive and defaulted: an omitted or null scope is
    ///     <see cref="ApprovalScope.Once" />, today's behaviour, so existing callers keep working unchanged.
    ///     <see cref="ApprovalScope.Session" /> is honoured only for the read-only skill tools on a locally authored
    ///     skill — the runner decides, and silently falls back to <see cref="ApprovalScope.Once" /> for anything else.
    ///     Ignored entirely on a deny.
    /// </summary>
    public ApprovalScope? Scope { get; init; }
}

public sealed class ResolveToolApprovalResponse
{
    public required string RequestId { get; init; }

    public required bool Approved { get; init; }
}

/// <summary>
///     The operator's answers to a pending <c>ask_user</c> question. <see cref="RequestId" /> is the question request id
///     the <c>question-requested</c> stream event carried (a plain string, not a Guid — the runner mints it as an opaque
///     key), which the runner uses to release the exact parked tool call. One entry per question in the call.
/// </summary>
public sealed class ResolveUserQuestionRequest
{
    public required string RequestId { get; init; }

    public required IReadOnlyList<ResolveUserQuestionAnswerDto> Answers { get; init; }
}

/// <summary>
///     One answered question. <see cref="Selected" /> carries the chosen option labels and <see cref="Other" /> the free
///     text from the client-appended "Other" row; both may be populated, but an answer with neither is rejected.
/// </summary>
public sealed class ResolveUserQuestionAnswerDto
{
    public required string Question { get; init; }

    public IReadOnlyList<string>? Selected { get; init; }

    public string? Other { get; init; }
}

/// <summary>
///     Deliberately echoes only the request id and how many answers were accepted — never the answers themselves, which
///     are the operator's words and stay out of both the response and the logs.
/// </summary>
public sealed class ResolveUserQuestionResponse
{
    public required string RequestId { get; init; }

    public required int AnswerCount { get; init; }
}

public sealed class BranchNodeChatConversationRequest
{
    public Guid ConversationId { get; init; }

    public Guid MessageId { get; init; }

    /// <summary>
    ///     Optional selected-revision map (<c>variantGroupId -&gt; selectedMessageId</c>, mirroring the persisted
    ///     selected-path shape) sent from the client's active-revision state so the branched thread matches the path
    ///     the user was viewing rather than always copying the newest revision. Null/empty ⇒ newest-per-group
    ///     (legacy). Validated server-side; an entry referencing a non-member message rejects the branch (400).
    /// </summary>
    public IReadOnlyDictionary<Guid, Guid>? SelectedRevisions { get; init; }
}

public sealed class ListNodeChatMessageRevisionsRequest
{
    public Guid ConversationId { get; init; }

    public Guid MessageId { get; init; }
}

public sealed class SetNodeChatMessageFeedbackRequest
{
    public Guid ConversationId { get; init; }

    public Guid MessageId { get; init; }

    public required string Rating { get; init; }

    public string? Comment { get; init; }
}

public sealed class GetNodeChatMessageFeedbackRequest
{
    public Guid ConversationId { get; init; }

    public Guid MessageId { get; init; }
}

public sealed class SetNodeChatSelectedPathRequest
{
    public Guid ConversationId { get; init; }

    public IReadOnlyDictionary<Guid, Guid>? SelectedPath { get; init; }
}

public sealed class NodeChatConversationSummaryResponse
{
    public required Guid ConversationId { get; init; }

    public string? Title { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long LastSeenUtc { get; init; }

    public string? LastMessagePreview { get; init; }

    public string? LastMessageStatus { get; init; }

    public required bool Purged { get; init; }

    public required string Origin { get; init; }

    public required bool IsPinned { get; init; }

    public required bool Archived { get; init; }
}

public sealed class NodeChatConversationResponse
{
    public required Guid ConversationId { get; init; }

    public string? Title { get; init; }

    public string? UserId { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long LastSeenUtc { get; init; }

    public required bool Purged { get; init; }

    public required string Origin { get; init; }

    public required bool IsPinned { get; init; }

    public required bool Archived { get; init; }

    public Guid? BranchOfConversationId { get; init; }

    public IReadOnlyDictionary<Guid, Guid>? SelectedPath { get; init; }

    /// <summary>
    ///     Temporary-chat flag (adaptive memory): when true this conversation's completed runs are NOT mined into new
    ///     memory candidates (write-only suppression — it still reads existing enabled memory). New conversations inherit
    ///     the bound agent's <c>DefaultTemporaryChat</c>; the operator can override it per-conversation.
    /// </summary>
    public required bool MemoryExcluded { get; init; }

    public required IReadOnlyList<NodeChatMessageResponse> Messages { get; init; }
}

public sealed class NodeChatMessageResponse
{
    public required Guid MessageId { get; init; }

    public required Guid ConversationId { get; init; }

    public Guid? RequestId { get; init; }

    public required int Sequence { get; init; }

    public required string Role { get; init; }

    public required string Content { get; init; }

    public string? Reasoning { get; init; }

    public required string Status { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }

    public required string Origin { get; init; }

    public string? Model { get; init; }

    public string? Error { get; init; }

    public int? InputTokens { get; init; }

    public int? OutputTokens { get; init; }

    public int? TotalTokens { get; init; }

    public int? ReasoningTokens { get; init; }

    public Guid? ParentMessageId { get; init; }

    public Guid? VariantGroupId { get; init; }

    public string? FeedbackRating { get; init; }

    public string? FeedbackComment { get; init; }

    /// <summary>
    ///     Ordered interleave of reasoning segments and tool cards (serialized as <c>parts</c>). Null for legacy
    ///     messages persisted before parts existed; the client synthesizes a single Thoughts block from
    ///     <see cref="Reasoning" /> in that case.
    /// </summary>
    public IReadOnlyList<NodeChatMessagePart>? Parts { get; init; }

    /// <summary>
    ///     The provenance of the agent that produced this assistant turn (per-response attribution). Null for legacy
    ///     turns persisted before agent mode existed and for user messages.
    /// </summary>
    public Guid? AgentDefinitionId { get; init; }

    /// <summary>
    ///     The display-name snapshot of the agent that produced this assistant turn (survives a later agent
    ///     rename/delete). Null for legacy turns and user messages; the client renders the localized "Default
    ///     Assistant" fallback label in that case.
    /// </summary>
    public string? AgentName { get; init; }

    /// <summary>
    ///     The reasoning effort actually used to generate this assistant turn (e.g. "none", "low", "medium", "high").
    ///     Null for legacy turns persisted before this field existed and for user messages.
    /// </summary>
    public string? ReasoningEffort { get; init; }

    /// <summary>
    ///     Whole-turn wall-clock generation duration in milliseconds, used with <see cref="OutputTokens" /> to compute
    ///     the optional tokens-per-second attribution. Null for legacy turns persisted before this field existed, the
    ///     platform path, and user messages.
    /// </summary>
    public long? GenerationDurationMs { get; init; }

    /// <summary>
    ///     Knowledge-base excerpts that grounded this plain-chat assistant turn, surfaced from the
    ///     metadata blob so the client can render a "Sources" strip. Null/absent for legacy turns, turns that did not use
    ///     the knowledge base, and user messages. Carries only non-sensitive provenance (document/chunk id, derived
    ///     title/section, score) — never chunk body text or the encrypted original file name.
    /// </summary>
    public IReadOnlyList<NodeChatMessageSource>? Sources { get; init; }
}

public sealed class NodeChatCancelMessageResponse
{
    public required Guid ConversationId { get; init; }

    public required Guid MessageId { get; init; }

    public required Guid RequestId { get; init; }

    public required string Status { get; init; }

    public required bool Cancelled { get; init; }
}

public sealed class NodeChatDeleteConversationResponse
{
    public required Guid ConversationId { get; init; }

    public required bool CancelRequested { get; init; }

    public required bool Purged { get; init; }
}

public sealed class NodeChatBranchConversationResponse
{
    public required Guid SourceConversationId { get; init; }

    public required Guid BranchedConversationId { get; init; }

    public required int CopiedMessageCount { get; init; }
}

public sealed class NodeChatMessageRevisionsResponse
{
    public required Guid MessageId { get; init; }

    public Guid? VariantGroupId { get; init; }

    public required IReadOnlyList<NodeChatMessageResponse> Variants { get; init; }
}

public sealed class NodeChatMessageFeedbackResponse
{
    public required Guid MessageId { get; init; }

    public required Guid ConversationId { get; init; }

    public required string Rating { get; init; }

    public string? Comment { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }
}

public sealed class NodeChatSelectedPathResponse
{
    public required Guid ConversationId { get; init; }

    public required IReadOnlyDictionary<Guid, Guid> SelectedPath { get; init; }
}

/// <summary>
///     409 Conflict body returned when a mutation targets a read-only (Origin=Remote) conversation.
/// </summary>
public sealed class NodeChatConflictResponse
{
    public required string Code { get; init; }

    public required string Reason { get; init; }

    /// <summary>Shared 409 body for read-only (Origin=Remote) conversation rejections.</summary>
    public static NodeChatConflictResponse ReadOnly { get; } = new()
    {
        Code = NodeChatReadOnlyConversationException.Code,
        Reason = NodeChatReadOnlyConversationException.Reason
    };
}

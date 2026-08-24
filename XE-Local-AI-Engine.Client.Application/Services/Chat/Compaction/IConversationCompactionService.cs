namespace XE_Local_AI_Engine.Client.Services.Chat.Compaction;

/// <summary>Why a compaction attempt ended the way it did.</summary>
public enum ConversationCompactionOutcome
{
    /// <summary>Older turns were folded into an updated synopsis and persisted.</summary>
    Compacted,

    /// <summary>The conversation id was not found (or is purged).</summary>
    ConversationNotFound,

    /// <summary>Every completed message is within the recent-keep window, or already covered by the existing synopsis — nothing to fold.</summary>
    NothingToCompact,

    /// <summary>No installed local (GGUF) chat model is available to summarize with; compaction stays fully on-node, so it cannot proceed.</summary>
    NoLocalModel,

    /// <summary>The summarizer produced no usable text; the existing synopsis (if any) was left untouched.</summary>
    SummarizerReturnedNothing
}

/// <summary>Outcome of a compaction attempt. Carries the new synopsis + how much it covers, so the endpoint can echo it back.</summary>
public sealed record ConversationCompactionResult(
    ConversationCompactionOutcome Outcome,
    string? Summary = null,
    int? CoversToSequence = null,
    int MessagesFolded = 0,
    long? UpdatedAtUtc = null,
    // The local model that actually produced the synopsis, and whether it differs from the model the user selected
    // (true only when a cloud/unknown selection was transparently downgraded to a node-local model). Lets the UI tell
    // the user their chat was summarized on-device instead of with their cloud selection.
    string? ModelUsed = null,
    bool UsedFallbackModel = false);

/// <summary>
///     Orchestrates non-destructive conversation compaction: selects the older span (everything before the recent-keep
///     window that the existing synopsis does not already cover), summarizes it with a node-local model, and persists the
///     synopsis. The original messages are never deleted — only what is SENT on later turns changes.
/// </summary>
public interface IConversationCompactionService
{
    /// <summary>
    ///     Compacts the conversation's older turns into (or extends) its synopsis. Idempotent when nothing new is
    ///     foldable. <paramref name="requestedModel" /> is the model the user is chatting with; it is used for
    ///     summarization when it is an installed LOCAL chat model, otherwise a node-local default is used so conversation
    ///     content never leaves the machine (a cloud/unknown selection degrades to local). Blank uses the node default.
    /// </summary>
    Task<ConversationCompactionResult> CompactAsync(Guid conversationId, string? requestedModel = null, CancellationToken cancellationToken = default) =>
        CompactAsync(conversationId, requestedModel, recentMessagesToKeepVerbatim: null, cancellationToken);

    /// <summary>
    ///     The same compaction with an explicit keep window. <paramref name="recentMessagesToKeepVerbatim" /> overrides
    ///     <see cref="ConversationCompactionOptions.RecentMessagesToKeepVerbatim" /> for this call only (clamped to the
    ///     same floor of 2), so a caller that knows its conversation does not depend on verbatim history can fold it
    ///     down to the last exchange. A work-session step is that caller: its state block is rebuilt from the database
    ///     every step, so the transcript beyond the previous step carries nothing the model still needs. Null keeps the
    ///     configured window, which is what the operator-driven chat compaction passes.
    /// </summary>
    Task<ConversationCompactionResult> CompactAsync(Guid conversationId,
        string? requestedModel,
        int? recentMessagesToKeepVerbatim,
        CancellationToken cancellationToken = default);
}

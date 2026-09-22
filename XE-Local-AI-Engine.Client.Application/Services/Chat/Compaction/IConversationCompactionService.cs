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
    SummarizerReturnedNothing,

    /// <summary>The operation exceeded the node's message-request budget; the previous synopsis was left untouched.</summary>
    TimedOut
}

/// <summary>Outcome of a compaction attempt. Carries the new synopsis + how much it covers, so the endpoint can echo it back.</summary>
public sealed class ConversationCompactionResult
{
    public required ConversationCompactionOutcome Outcome { get; init; }

    public string? Summary { get; init; }

    public int? CoversToSequence { get; init; }

    public int MessagesFolded { get; init; }

    public long? UpdatedAtUtc { get; init; }

    // The local model that produced the synopsis, and whether it differs from the user's selection, which is true
    // only on a downgrade, so the UI can say the chat was summarized on-device.
    public string? ModelUsed { get; init; }

    public bool UsedFallbackModel { get; init; }
}

/// <summary>
///     Orchestrates non-destructive conversation compaction: it selects the older span the existing synopsis does
///     not cover, summarizes it with a node-local model and persists the result.
/// </summary>
/// <remarks>The original messages are never deleted; only what is SENT on later turns changes.</remarks>
public interface IConversationCompactionService
{
    /// <summary>
    ///     Compacts the conversation's older turns into its synopsis, or extends one, and is idempotent when nothing
    ///     new is foldable.
    /// </summary>
    /// <param name="requestedModel">The model to summarize with, if it is an installed LOCAL chat model.</param>
    /// <remarks>Anything else, including a blank, degrades to a node-local default, so content stays on-machine.</remarks>
    Task<ConversationCompactionResult> CompactAsync(Guid conversationId, string? requestedModel = null, CancellationToken cancellationToken = default) =>
        CompactAsync(conversationId, requestedModel, recentMessagesToKeepVerbatim: null, cancellationToken);

    /// <summary>
    ///     The same compaction with an explicit keep window.
    /// </summary>
    /// <param name="recentMessagesToKeepVerbatim">
    ///     Overrides <see cref="ConversationCompactionOptions.RecentMessagesToKeepVerbatim" /> for this call only,
    ///     clamped to the same floor of 2; null keeps the configured window.
    /// </param>
    /// <remarks>
    ///     A caller that knows its conversation does not depend on verbatim history can fold down to the last
    ///     exchange. A work-session step is that caller: its state block is rebuilt from the database every step, so
    ///     the transcript beyond the previous one carries nothing the model still needs.
    /// </remarks>
    Task<ConversationCompactionResult> CompactAsync(Guid conversationId,
        string? requestedModel,
        int? recentMessagesToKeepVerbatim,
        CancellationToken cancellationToken = default);
}

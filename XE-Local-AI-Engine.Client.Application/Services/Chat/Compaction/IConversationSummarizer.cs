namespace XE_Local_AI_Engine.Client.Services.Chat.Compaction;

/// <summary>One older-history message handed to the summarizer (role + already-decrypted content).</summary>
public sealed record ConversationSummarizerMessage(string Role, string Content);

/// <summary>Input for one summarization pass: an optional prior synopsis to fold in, plus the newer older-history span.</summary>
/// <param name="SupportsThinking">
///     Whether <paramref name="ModelName" /> advertises graded thinking, as resolved by
///     <c>IModelCapabilityResolver</c>. False — the safe default, matching that resolver's own miss behaviour — means
///     the fold sends no thinking fields at all.
/// </param>
public sealed record ConversationSummarizerInput(string? PriorSummary,
    IReadOnlyList<ConversationSummarizerMessage> Messages,
    string ModelName,
    bool SupportsThinking = false);

/// <summary>
///     Produces a compact, prose synopsis of an older conversation span using a NODE-LOCAL model only (never the shared
///     cloud-capable client) — the same privacy invariant the memory-extraction agent holds: conversation content only
///     ever reaches a per-run <c>provider.CreateChatClient(...)</c> client. The synopsis merges any prior synopsis with
///     the newer span so repeated compaction is incremental rather than lossy.
/// </summary>
public interface IConversationSummarizer
{
    /// <summary>
    ///     Summarizes <paramref name="input" /> into a single synopsis string. Returns null when the model produced no
    ///     usable text (the caller then leaves the existing synopsis untouched rather than clobbering it).
    /// </summary>
    Task<string?> SummarizeAsync(ConversationSummarizerInput input, CancellationToken cancellationToken = default);
}

namespace XE_Local_AI_Engine.Client.Services.Chat.Compaction;

/// <summary>One older-history message handed to the summarizer (role + already-decrypted content).</summary>
public sealed class ConversationSummarizerMessage
{
    public required string Role { get; init; }

    public required string Content { get; init; }
}

/// <summary>Input for one summarization pass: an optional prior synopsis to fold in, plus the newer older-history span.</summary>
public sealed class ConversationSummarizerInput
{
    public required string? PriorSummary { get; init; }

    /// <summary>
    ///     The rendered live structured state: facts kept elsewhere that the synopsis must not repeat. Null sends none;
    ///     the summarizer trims it by whole lines when it would not leave room for progress.
    /// </summary>
    public string? AlreadyCaptured { get; init; }

    public required IReadOnlyList<ConversationSummarizerMessage> Messages { get; init; }

    public required string ModelName { get; init; }

    /// <summary>
    ///     Whether <see cref="ModelName" /> advertises graded thinking, as resolved by
    ///     <c>IModelCapabilityResolver</c>. False — the safe default, matching that resolver's own miss behaviour — means
    ///     the fold sends no thinking fields at all.
    /// </summary>
    public bool SupportsThinking { get; init; }

    /// <summary>
    ///     The context window, in tokens, <see cref="ModelName" /> is running with, read back from the warm runtime the
    ///     way a chat turn reads it. Null means unknown, and the fold budget stays at the configured ceiling.
    /// </summary>
    public int? EffectiveContextTokens { get; init; }
}

/// <summary>
///     Produces a compact, prose synopsis of an older conversation span using a NODE-LOCAL model only, merging any
///     prior synopsis with the newer span so repeated compaction is incremental rather than lossy.
/// </summary>
/// <remarks>
///     It holds the same privacy invariant as the memory-extraction agent: conversation content only ever reaches a
///     per-run <c>provider.CreateChatClient(...)</c> client, never the shared cloud-capable one.
/// </remarks>
public interface IConversationSummarizer
{
    /// <summary>
    ///     Summarizes <paramref name="input" /> into a single synopsis string. Returns null when the model produced no
    ///     usable text (the caller then leaves the existing synopsis untouched rather than clobbering it).
    /// </summary>
    Task<string?> SummarizeAsync(ConversationSummarizerInput input, CancellationToken cancellationToken = default);
}

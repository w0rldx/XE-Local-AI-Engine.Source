namespace XE_Local_AI_Engine.Client.Services.Chat.Compaction;

using System.ComponentModel.DataAnnotations;

/// <summary>
///     Operator-tunable knobs for manual, non-destructive conversation compaction (local-model summarization). Bound from
///     the <c>Agent:ConversationCompaction</c> section. Compaction folds the older turns of a conversation into an
///     encrypted synopsis that is sent in their place, so a long chat keeps its gist within the context window without
///     the originals ever being deleted.
/// </summary>
public sealed class ConversationCompactionOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string SectionName = "Agent:ConversationCompaction";

    /// <summary>
    ///     How many of the most recent completed messages are always kept verbatim and never folded into the synopsis, so
    ///     compaction only ever condenses older history. Must be at least 2 so the latest user turn and its answer survive.
    /// </summary>
    [Range(2, int.MaxValue)]
    public int RecentMessagesToKeepVerbatim { get; set; } = 8;

    /// <summary>
    ///     Upper bound on the synopsis length the summarizer is asked to produce (characters). A guard so a runaway model
    ///     cannot emit a synopsis larger than the span it replaced; the service truncates anything longer.
    /// </summary>
    [Range(256, int.MaxValue)]
    public int MaxSummaryChars { get; set; } = 4000;

    /// <summary>
    ///     Character budget for the fold span sent to the model in a SINGLE summarization call. When the older history is
    ///     larger than this — the oversized-conversation case compaction most needs to handle — the summarizer folds it in
    ///     multiple passes (running summary + next batch) so no single provider request exceeds the model's context
    ///     window. Deliberately conservative so even a 4096-token model has room for the running summary + system prompt +
    ///     reserved output alongside the batch (≈ <see cref="MaxSummaryChars" /> + this + overhead stays well under 4096
    ///     tokens at ~4 chars/token).
    ///     <para>
    ///         ponytail: fixed char budget, not the model's probed effective window — upgrade to per-model window probing
    ///         if a large-context model should fold in fewer passes.
    ///     </para>
    /// </summary>
    [Range(512, int.MaxValue)]
    public int MaxInputCharsPerSummarizationCall { get; set; } = 6000;
}

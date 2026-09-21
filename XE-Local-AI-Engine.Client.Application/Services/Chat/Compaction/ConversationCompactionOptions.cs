namespace XE_Local_AI_Engine.Client.Services.Chat.Compaction;

using System.ComponentModel.DataAnnotations;

/// <summary>
///     Operator-tunable knobs for manual, non-destructive conversation compaction, bound from the
///     <c>Agent:ConversationCompaction</c> section.
/// </summary>
/// <remarks>
///     Compaction folds a conversation's older turns into an encrypted synopsis sent in their place, so a long chat
///     keeps its gist within the context window without the originals ever being deleted.
/// </remarks>
public sealed class ConversationCompactionOptions : IValidatableObject
{
    public const string SectionName = "Agent:ConversationCompaction";

    /// <summary>Smallest useful synopsis cap.</summary>
    public const int MinimumSummaryChars = 256;

    /// <summary>
    ///     Largest supported synopsis cap. The synopsis is deliberately kept compact; increasing the total request budget
    ///     allows larger source batches, not an unbounded running summary that consumes the next fold request.
    /// </summary>
    public const int MaximumSummaryChars = 4_000;

    /// <summary>
    ///     Smallest supported total request budget. This leaves room for the fixed prompt, the minimum synopsis cap, and
    ///     source content; <see cref="Validate" /> enforces the exact prompt-dependent requirement for each configuration.
    /// </summary>
    public const int MinimumInputCharsPerSummarizationCall = 2_000;

    /// <summary>
    ///     How many of the most recent completed messages are always kept verbatim and never folded into the synopsis, so
    ///     compaction only ever condenses older history. Must be at least 2 so the latest user turn and its answer survive.
    /// </summary>
    [Range(2, int.MaxValue)]
    public int RecentMessagesToKeepVerbatim { get; set; } = 8;

    /// <summary>
    ///     Upper bound in characters on the synopsis the summarizer is asked to produce, so a runaway model cannot
    ///     emit one larger than the span it replaced; the service truncates anything longer.
    /// </summary>
    /// <remarks>
    ///     The supported range is 256 through 4,000 characters, so a running synopsis cannot consume an unbounded
    ///     share of each later fold request.
    /// </remarks>
    [Range(MinimumSummaryChars, MaximumSummaryChars)]
    public int MaxSummaryChars { get; set; } = 4000;

    /// <summary>
    ///     Total character budget for the model-facing messages of a SINGLE summarization call: the system prompt
    ///     plus the serialized JSON carrying the running summary and the source-message batch.
    /// </summary>
    /// <remarks>
    ///     Older history larger than this, including one individually oversized message, folds in multiple passes so
    ///     no provider request exceeds the bound. The default leaves at least 6,500 characters of source room per
    ///     fold even with the running summary at its cap, which is what keeps a long conversation from folding in
    ///     dozens of lossy passes. The summarizer never probes the model's window, so lower this on a 4k-token model.
    ///     simplified: a fixed budget, not a probed window — upgrade to per-model probing to fold in fewer passes.
    /// </remarks>
    [Range(MinimumInputCharsPerSummarizationCall, int.MaxValue)]
    public int MaxInputCharsPerSummarizationCall { get; set; } = 12_000;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (MaxSummaryChars > 0
            && MaxInputCharsPerSummarizationCall < ConversationSummarizer.GetMinimumRequestBudget(MaxSummaryChars))
        {
            yield return new ValidationResult("The total request character budget must fit the system prompt, the maximum intermediate summary, and at least one message Rune.");
        }
    }
}

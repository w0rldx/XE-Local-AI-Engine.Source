namespace XE_Local_AI_Engine.Client.Services.Chat.Compaction;

using System.ComponentModel.DataAnnotations;

/// <summary>
///     Operator-tunable knobs for manual and automatic non-destructive conversation compaction, bound from the
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
    ///     dozens of lossy passes. This is a ceiling: the summarizer lowers each call's budget to 60% of the fold
    ///     model's effective window in calibrated characters when that window is known.
    /// </remarks>
    [Range(MinimumInputCharsPerSummarizationCall, int.MaxValue)]
    public int MaxInputCharsPerSummarizationCall { get; set; } = 12_000;

    /// <summary>
    ///     Whether a completed chat turn queues a compaction when the next turn's replayed history crosses
    ///     <see cref="AutoCompactFraction" /> of the turn's usable window. The manual control works either way.
    /// </summary>
    public bool AutoCompactEnabled { get; set; } = true;

    /// <summary>
    ///     The share of the turn's usable window (context capacity minus reserved output) the projected next-turn
    ///     history may fill before a post-turn compaction is queued.
    /// </summary>
    [Range(0.3, 0.95)]
    public double AutoCompactFraction { get; set; } = 0.75;

    /// <summary>Bound on queued background maintenance jobs; a full queue drops the newest with a warning.</summary>
    [Range(1, int.MaxValue)]
    public int MaintenanceQueueCapacity { get; set; } = 64;

    /// <summary>How long shutdown waits for queued and running maintenance jobs before cancelling them.</summary>
    [Range(1, int.MaxValue)]
    public int MaintenanceShutdownDrainTimeoutSeconds { get; set; } = 10;

    /// <summary>
    ///     Whether completed turns are distilled into the conversation's structured state (goals, decisions,
    ///     corrections, open questions) that is injected into later turns alongside the synopsis.
    /// </summary>
    public bool DistillEnabled { get; set; } = true;

    /// <summary>Estimated tokens of new completed messages that trigger a distillation pass.</summary>
    [Range(500, int.MaxValue)]
    public int DistillEveryTokens { get; set; } = 3000;

    /// <summary>Count of new completed messages that triggers a distillation pass even below the token threshold.</summary>
    [Range(1, int.MaxValue)]
    public int DistillEveryMessages { get; set; } = 6;

    /// <summary>Upper bound on entries kept in a conversation's state; the reducer drops the least valuable first.</summary>
    [Range(10, 500)]
    public int MaxStateEntries { get; set; } = 60;

    /// <summary>Upper bound on the summed characters of all entry values in a conversation's state.</summary>
    [Range(1000, 50_000)]
    public int MaxStateChars { get; set; } = 6000;

    /// <summary>Output-token cap for one distiller call, so a runaway model cannot emit an unbounded delta.</summary>
    [Range(256, 8192)]
    public int DistillerMaxOutputTokens { get; set; } = 1024;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (MaxSummaryChars > 0
            && MaxInputCharsPerSummarizationCall < ConversationSummarizer.GetMinimumRequestBudget(MaxSummaryChars))
        {
            yield return new ValidationResult("The total request character budget must fit the system prompt, the maximum intermediate summary, and at least one message Rune.");
        }
    }
}

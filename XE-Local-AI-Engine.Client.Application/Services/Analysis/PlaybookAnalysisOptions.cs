namespace XE_Local_AI_Engine.Client.Services.Analysis;

/// <summary>
///     Options for the analysis staging analysis agent. <see cref="ModelName" /> names the node-local model used to read
///     feedback (defaulted in composition to the node's configured chat model, so analysis never silently picks a
///     cloud model); <see cref="MaxProposals" /> caps how many actions a single run may propose.
/// </summary>
public sealed class PlaybookAnalysisOptions
{
    public const string Section = "PlaybookAnalysis";

    /// <summary>The node-local model used for analysis. Defaulted from the node chat model at composition time.</summary>
    public string ModelName { get; set; } = string.Empty;

    /// <summary>Upper bound on proposals per run (prompt-bloat / review-load guard).</summary>
    public int MaxProposals { get; set; } = 5;

    /// <summary>Default injection priority assigned to a newly-suggested action (sorts after typical manual actions).</summary>
    public int SuggestionPriority { get; set; } = 100;
}

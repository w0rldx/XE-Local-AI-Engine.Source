namespace XE_Local_AI_Engine.Client.Services.Analysis;

/// <summary>Options for the analysis staging analysis agent.</summary>
/// <remarks>
///     <see cref="ModelName" /> only seeds the <c>PlaybookAnalysisModelName</c> node setting, which consumers read; blank inherits the
///     default model, so analysis never silently picks a cloud model. <see cref="MaxProposals" /> caps the actions one run may propose.
/// </remarks>
public sealed class PlaybookAnalysisOptions
{
    public const string Section = "PlaybookAnalysis";

    /// <summary>Appsettings seed of the <c>PlaybookAnalysisModelName</c> node setting.</summary>
    public string ModelName { get; set; } = string.Empty;

    /// <summary>Upper bound on proposals per run (prompt-bloat / review-load guard).</summary>
    public int MaxProposals { get; set; } = 5;

    /// <summary>Default injection priority assigned to a newly-suggested action (sorts after typical manual actions).</summary>
    public int SuggestionPriority { get; set; } = 100;
}

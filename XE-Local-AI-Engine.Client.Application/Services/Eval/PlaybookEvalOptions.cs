namespace XE_Local_AI_Engine.Client.Services.Eval;

/// <summary>
///     Options for golden-conversation evaluation.
/// </summary>
/// <remarks>
///     <see cref="ModelName" /> only seeds the <c>PlaybookEvalModelName</c> node setting, which consumers read; blank inherits the
///     default model, so the eval never silently picks a cloud one.
///     <see cref="MaxGoldenCases" /> caps how many cases one run evaluates, so a large set cannot unbound the cost.
/// </remarks>
public sealed class PlaybookEvalOptions
{
    public const string Section = "PlaybookEval";

    /// <summary>Appsettings seed of the <c>PlaybookEvalModelName</c> node setting.</summary>
    public string ModelName { get; set; } = string.Empty;

    /// <summary>Upper bound on golden cases evaluated per run (batch-cost guard; truncation is logged, never silent).</summary>
    public int MaxGoldenCases { get; set; } = 25;

    /// <summary>
    ///     Reasoning effort for the eval run, from the ordinary vocabulary and never <c>auto</c>; null by default.
    /// </summary>
    /// <remarks>
    ///     A value is forwarded to <c>IPlaybookEvalAgentRunner.RunAsync</c>, so a sweep can compare one effort
    ///     against another.
    /// </remarks>
    public string? ReasoningEffort { get; set; }
}

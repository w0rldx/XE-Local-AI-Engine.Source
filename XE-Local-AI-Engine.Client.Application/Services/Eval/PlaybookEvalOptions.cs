namespace XE_Local_AI_Engine.Client.Services.Eval;

/// <summary>
///     Options for golden-conversation evaluation. <see cref="ModelName" /> names the node-local model used to re-run the
///     agent loop and to score judge-path golden cases (defaulted in composition to the node's configured chat model,
///     so the eval never silently picks a cloud model); <see cref="MaxGoldenCases" /> caps how many golden cases a
///     single run evaluates so a large set cannot unbound batch cost.
/// </summary>
public sealed class PlaybookEvalOptions
{
    public const string Section = "PlaybookEval";

    /// <summary>The node-local model used for the eval run + judge. Defaulted from the node chat model at composition time.</summary>
    public string ModelName { get; set; } = string.Empty;

    /// <summary>Upper bound on golden cases evaluated per run (batch-cost guard; truncation is logged, never silent).</summary>
    public int MaxGoldenCases { get; set; } = 25;

    /// <summary>
    ///     Reasoning effort for the eval run, from the ordinary vocabulary (never <c>auto</c>). Null by default, which
    ///     leaves the run exactly as it was before this setting existed; a value is forwarded to
    ///     <c>IPlaybookEvalAgentRunner.RunAsync</c> so a sweep can compare one effort against another.
    /// </summary>
    public string? ReasoningEffort { get; set; }
}

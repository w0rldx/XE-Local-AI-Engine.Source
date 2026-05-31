namespace XE_Local_AI_Engine.Client.Services.Eval;

/// <summary>
///     Options for the Playbook P4 eval gate. <see cref="ModelName" /> names the node-local model used to re-run the
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
}

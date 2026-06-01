namespace XE_Local_AI_Engine.Client.Services.Eval;

/// <summary>
///     Options for the golden harvest follow-up (deterministic, no model). <see cref="MaxProposals" /> hard-caps how
///     many candidates a single harvest run persists (review-load + write-cost guard); <see cref="MaxThumbsUpScan" />
///     caps how many most-recent thumbs-up sources the read boundary scans per run. No model name — harvest invokes no
///     LLM (D1), so unlike the eval/analysis options there is nothing to default at composition time.
/// </summary>
public sealed class GoldenHarvestOptions
{
    public const string Section = "GoldenHarvest";

    /// <summary>Upper bound on candidates persisted per run (review-load / write-cost guard).</summary>
    public int MaxProposals { get; set; } = 10;

    /// <summary>Upper bound on most-recent thumbs-up sources scanned per run.</summary>
    public int MaxThumbsUpScan { get; set; } = 50;
}

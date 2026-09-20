namespace XE_Local_AI_Engine.Client.Services.Eval;

/// <summary>
///     Options for deterministic golden harvest runs: <see cref="MaxProposals" /> hard-caps the candidates one run
///     persists, and <see cref="MaxThumbsUpScan" /> the most-recent thumbs-up sources the read boundary scans.
/// </summary>
/// <remarks>
///     There is no model name, because harvest invokes no LLM, so unlike the eval and analysis options nothing needs
///     defaulting at composition time.
/// </remarks>
public sealed class GoldenHarvestOptions
{
    public const string Section = "GoldenHarvest";

    /// <summary>Upper bound on candidates persisted per run (review-load / write-cost guard).</summary>
    public int MaxProposals { get; set; } = 10;

    /// <summary>Upper bound on most-recent thumbs-up sources scanned per run.</summary>
    public int MaxThumbsUpScan { get; set; } = 50;
}

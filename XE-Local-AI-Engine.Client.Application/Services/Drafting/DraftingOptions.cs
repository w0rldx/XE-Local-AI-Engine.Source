namespace XE_Local_AI_Engine.Client.Services.Drafting;

/// <summary>
///     Bounds for AI-assisted drafting. Every value is a ceiling: an oversized or hostile request must never be able to
///     hold the single draft slot for minutes. Mirrors <c>MemoryExtractionOptions</c>' shape (bound in
///     <c>AddNodeDraftingExtensions</c>, clamped in its <c>PostConfigure</c>).
/// </summary>
public sealed class DraftingOptions
{
    public const string Section = "Drafting";

    /// <summary>
    ///     Aggregate character ceiling across every prompt part (brief + existing name/description/content). Checked
    ///     BEFORE the admission gate is acquired, so a too-large request is rejected without occupying the slot.
    /// </summary>
    public int MaxPromptChars { get; set; } = 60000;

    /// <summary>Hard cap on generated tokens, so a runaway generation cannot run to the full timeout.</summary>
    public int MaxOutputTokens { get; set; } = 8192;

    /// <summary>
    ///     Wall-clock budget for one generation. Generous because a cold local model pays load time on first use; the
    ///     operator sees elapsed time and can cancel. Elapsing yields a typed failure with the gate released.
    /// </summary>
    public TimeSpan GenerationTimeout { get; set; } = TimeSpan.FromSeconds(300);
}

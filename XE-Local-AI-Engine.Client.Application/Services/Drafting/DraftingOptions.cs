namespace XE_Local_AI_Engine.Client.Services.Drafting;

/// <summary>
///     Bounds for AI-assisted drafting: every value is a ceiling, so an oversized or hostile request can never hold
///     the single draft slot for minutes.
/// </summary>
/// <remarks>It mirrors <c>MemoryExtractionOptions</c>' shape, bound and clamped in the same extension.</remarks>
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
    ///     Wall-clock budget for one generation, generous because a cold local model pays load time on first use;
    ///     elapsing yields a typed failure with the gate released.
    /// </summary>
    /// <remarks>
    ///     <see langword="null" />, the default, follows the node-level <em>Maximum message request timeout</em>:
    ///     drafting is a model request like any other, and that knob is read LIVE per generation, so a Save takes
    ///     effect without a restart. Set an explicit value only to impose a drafting-specific ceiling, because one
    ///     silently pre-empts a raised node setting.
    /// </remarks>
    public TimeSpan? GenerationTimeout { get; set; }
}

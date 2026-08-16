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
    ///     <para>
    ///         <see langword="null" /> (the default) means "follow the node-level <em>Maximum message request
    ///         timeout</em>": drafting is a model request like any other, and the operator's knob is read LIVE per
    ///         generation, so a Save takes effect without a node restart. Set an explicit value only to impose a
    ///         drafting-specific ceiling — the previous hardcoded 300 s silently pre-empted a raised node setting.
    ///     </para>
    /// </summary>
    public TimeSpan? GenerationTimeout { get; set; }
}

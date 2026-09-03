namespace XE_Local_AI_Engine.Client.Services.ModelFit.Fit;

/// <summary>
///     Names a model's attention shape from GGUF numbers alone — never from the architecture string, which is a label an
///     author chooses and not a fact about the cache. The tag is presentation and a ranking tiebreak's companion; it
///     never gates a launch.
/// </summary>
public static class AttentionArchTag
{
    /// <summary>Multi-head Latent Attention: one latent K tensor per layer and no V cache at all (deepseek2).</summary>
    public const string Mla = "mla";

    /// <summary>Interleaved sliding-window attention: only every Nth layer holds the full context (Gemma family).</summary>
    public const string Swa = "swa";

    /// <summary>Grouped-query attention: fewer KV heads than query heads, so the cache is a fraction of the MHA size.</summary>
    public const string Gqa = "gqa";

    /// <summary>Plain multi-head attention — also the answer when the header does not say enough to claim anything else.</summary>
    public const string Mha = "mha";

    /// <summary>
    ///     Resolves the tag for one file's geometry. The head counts are explicit parameters because
    ///     <see cref="GgufAttentionShape" /> carries neither, and three of the four answers cannot be decided without
    ///     them. Order matters: MLA is checked first because an MLA model's other numbers still look ordinary, and an
    ///     unknown head count falls through to <see cref="Mha" /> rather than guessing.
    /// </summary>
    public static string Resolve(GgufAttentionShape? shape, long? headCount, long? headCountKv)
    {
        if (shape?.IsMla == true)
        {
            return Mla;
        }

        if (shape?.SlidingWindow is > 0 && shape.SlidingWindowPattern is >= 1)
        {
            return Swa;
        }

        return headCountKv is > 0 && headCount is > 0 && headCountKv < headCount ? Gqa : Mha;
    }
}

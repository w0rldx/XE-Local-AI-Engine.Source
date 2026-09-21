namespace XE_Local_AI_Engine.AI.Agent.Configuration;

using System.ComponentModel.DataAnnotations;

/// <summary>
///     Node options for the per-round tool-relevance offer: above <see cref="Threshold" /> offered tools the send-time
///     hop narrows the <c>tools</c> array to an always-on core plus a relevance-ranked fill.
/// </summary>
/// <remarks>
///     <b>Off by default.</b> With the node setting <c>ToolRelevanceEnabled</c> off the hop is a reference-equality
///     pass-through and <c>list_tools</c> is never appended, so every prompt, tool offer and runtime config hash is
///     byte-identical. The filter is a context budget and never an authorisation boundary: hiding a tool neither
///     widens nor narrows what the agent may call. See docs/wiki/04-agent-mode.md ("Tool-relevance narrowing").
/// </remarks>
public sealed class ToolRelevanceOptions
{
    public const string Section = "Agent:ToolRelevance";

    /// <summary>
    ///     Filtering engages for an agent carrying more than this many RESOLVED tools, core included; at or below it
    ///     the whole array is sent unchanged.
    /// </summary>
    /// <remarks>
    ///     "Resolved" is the operator-facing count deliberately: the array the model receives also carries the
    ///     <c>list_tools</c> escape hatch, so the first filterable array holds one more entry than this number. It is a
    ///     TRIGGER, not a cap — because <see cref="MinimumRankedSlots" /> floors the ranked fill, an agent with a large
    ///     core may still be offered more tools than this.
    /// </remarks>
    [Range(1, 1000)]
    public int Threshold { get; set; } = 12;

    /// <summary>
    ///     Floor on the non-core slots the ranker fills once filtering engages; the fill is
    ///     <c>max(Threshold - core.Count, MinimumRankedSlots)</c>.
    /// </summary>
    /// <remarks>
    ///     Without the floor a skills-bearing work-session agent (core ~9) would have roughly three rankable slots
    ///     left, which is not a context saving worth a hidden-tool risk. The consequence is deliberate: the offered
    ///     array may exceed <see cref="Threshold" />.
    /// </remarks>
    [Range(1, 1000)]
    public int MinimumRankedSlots { get; set; } = 6;

    /// <summary>Node-local embedding model used to rank the non-core candidates.</summary>
    /// <remarks>
    ///     Null or empty (the default) keeps the model-free lexical selector as the effective ranker; any embedding
    ///     failure also falls back to lexical, so a send never breaks and CI stays deterministic without a running
    ///     embedding process.
    /// </remarks>
    public string? EmbeddingModelName { get; set; }

    /// <summary>Provider key for the embedding model; must match a registered node-local provider (default "llamacpp").</summary>
    public string EmbeddingProviderName { get; set; } = "llamacpp";

    /// <summary>Upper bound on the in-memory candidate-embedding cache (RAM-only, never persisted). Floored at 1.</summary>
    [Range(1, int.MaxValue)]
    public int EmbeddingCacheMaxEntries { get; set; } = 512;

    /// <summary>
    ///     Hard bound on the embedding round-trip, applied by the embedding selector with its own linked
    ///     <c>CancelAfter</c>.
    /// </summary>
    /// <remarks>
    ///     Unlike the playbook ranker this selection runs INSIDE the send, in front of the first token, so an expiry
    ///     degrades to the lexical ranker rather than failing the turn.
    /// </remarks>
    public TimeSpan EmbeddingTimeout { get; set; } = TimeSpan.FromSeconds(2);
}

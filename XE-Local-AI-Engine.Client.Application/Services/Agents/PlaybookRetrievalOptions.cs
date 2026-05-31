namespace XE_Local_AI_Engine.Client.Services.Agents;

/// <summary>
///     Options for the Playbook P5 relevance-retrieval path. When an agent has more than
///     <see cref="RetrievalThreshold" /> Enabled actions and the incoming send carries a non-blank query, the resolver
///     injects only the top <see cref="TopK" /> most relevant actions instead of the full static prepend; at or below the
///     threshold (or with a blank query) the pre-P5 static-prepend behaviour is preserved byte-for-byte.
/// </summary>
public sealed class PlaybookRetrievalOptions
{
    public const string Section = "PlaybookRetrieval";

    /// <summary>Enabled-action count above which relevance retrieval engages (at or below it, static prepend is used).</summary>
    public int RetrievalThreshold { get; set; } = 8;

    /// <summary>Maximum number of actions injected per send once retrieval engages.</summary>
    public int TopK { get; set; } = 8;
}

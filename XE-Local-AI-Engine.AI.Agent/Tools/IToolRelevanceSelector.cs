namespace XE_Local_AI_Engine.AI.Agent.Tools;

/// <summary>
///     One tool as the relevance selector sees it: its name, its model-visible description, and whether the always-on
///     core set claims it. NAMES cross this boundary, never <c>AITool</c>s, so a selector can never hold — let alone
///     invoke — an executable.
/// </summary>
/// <param name="Name">The tool name as it appears in the outbound <c>tools</c> array.</param>
/// <param name="Description">The model-visible description, or <see langword="null" /> when the tool carries none.</param>
/// <param name="IsCore">
///     Whether the tool is always offered. Core tools are never ranked and never trimmed. Tool AUTHORISATION is never
///     an input here: the core set is a fixed, node-wide name set, and an approval policy edit must not reshape which
///     tools a model is shown.
/// </param>
public sealed record ToolRelevanceCandidate(string Name, string? Description, bool IsCore);

/// <summary>
///     One selection decision: which names to offer this turn, and which were held back. Both lists are in the input
///     candidate order, so a fixed selected set always serialises to the same <c>tools</c> array — which is what keeps
///     the llama.cpp prompt prefix and its compiled GBNF grammar stable across the rounds of one turn.
/// </summary>
public sealed record ToolRelevanceSelection(IReadOnlyList<string> OfferedNames, IReadOnlyList<string> HiddenNames)
{
    /// <summary>
    ///     Builds the selection from the ranked non-core picks by re-imposing the INPUT order over
    ///     <c>core union selected</c>. Every selector shares this step: it is what makes a fixed selected set serialise to
    ///     the same array whatever order the ranker produced it in, and neither the lexical nor the embedding selector
    ///     gets to have its own opinion about it.
    /// </summary>
    /// <param name="candidates">The candidates, in the outbound array's own order.</param>
    /// <param name="selectedNonCore">Indices into <paramref name="candidates" /> the ranker picked; core is implicit.</param>
    public static ToolRelevanceSelection Compose(IReadOnlyList<ToolRelevanceCandidate> candidates, IReadOnlySet<int> selectedNonCore)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(selectedNonCore);

        var offered = new List<string>(candidates.Count);
        var hidden = new List<string>();
        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            if (candidate.IsCore || selectedNonCore.Contains(index))
            {
                offered.Add(candidate.Name);
            }
            else
            {
                hidden.Add(candidate.Name);
            }
        }

        return new ToolRelevanceSelection(offered, hidden);
    }
}

/// <summary>
///     Chooses the subset of an agent's tools to put in front of the model for one turn. Implementations are
///     model-free by default (<c>LexicalToolRelevanceSelector</c>); the node may replace the registration with an
///     embedding-backed one, which degrades to the lexical ranking on any failure or timeout.
/// </summary>
public interface IToolRelevanceSelector
{
    /// <summary>
    ///     Selects the tools to offer. At or below <paramref name="threshold" /> candidates, or with a
    ///     <paramref name="query" /> that carries no content word, every name is returned and nothing is hidden WITHOUT
    ///     the ranker being invoked.
    /// </summary>
    /// <param name="query">
    ///     The turn's relevance query (the last user message's text). Blank — and, for the lexical selector, a query
    ///     that is nothing but function words — takes the fast path.
    /// </param>
    /// <param name="candidates">Every offered tool, in the order the outbound array carries them.</param>
    /// <param name="threshold">Candidate count above which ranking engages. A trigger, not a cap.</param>
    /// <param name="cancellationToken">Cancels the selection.</param>
    Task<ToolRelevanceSelection> SelectAsync(string? query,
        IReadOnlyList<ToolRelevanceCandidate> candidates,
        int threshold,
        CancellationToken cancellationToken);
}

namespace XE_Local_AI_Engine.AI.Agent.Tools;

/// <summary>
///     One tool as the relevance selector sees it: its name, its model-visible description, and whether the always-on
///     core set claims it.
/// </summary>
/// <remarks>
///     NAMES cross this boundary, never <c>AITool</c>s, so a selector can never hold — let alone invoke — an
///     executable.
/// </remarks>
public sealed record ToolRelevanceCandidate
{
    /// <summary>The tool name as it appears in the outbound <c>tools</c> array.</summary>
    public required string Name { get; init; }

    /// <summary>The model-visible description, or <see langword="null" /> when the tool carries none.</summary>
    public required string? Description { get; init; }

    /// <summary>Whether the tool is always offered; core tools are never ranked and never trimmed.</summary>
    /// <remarks>
    ///     Tool AUTHORISATION is never an input here: the core set is a fixed, node-wide name set, and an approval
    ///     policy edit must not reshape which tools a model is shown.
    /// </remarks>
    public required bool IsCore { get; init; }
}

/// <summary>One selection decision: which names to offer this turn, and which were held back.</summary>
/// <remarks>
///     Both lists are in the input candidate order, so a fixed selected set always serialises to the same <c>tools</c>
///     array — which is what keeps the llama.cpp prompt prefix and its compiled GBNF grammar stable across the rounds
///     of one turn.
/// </remarks>
public sealed class ToolRelevanceSelection
{
    public required IReadOnlyList<string> OfferedNames { get; init; }

    public required IReadOnlyList<string> HiddenNames { get; init; }

    /// <summary>
    ///     Builds the selection from the ranked non-core picks by re-imposing the INPUT order over the union of core
    ///     and selected.
    /// </summary>
    /// <remarks>
    ///     Every selector shares this step: it is what makes a fixed selected set serialise to the same array whatever
    ///     order the ranker produced it in, and neither the lexical nor the embedding selector gets an opinion about it.
    /// </remarks>
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

        return new ToolRelevanceSelection { OfferedNames = offered, HiddenNames = hidden };
    }
}

/// <summary>Chooses the subset of an agent's tools to put in front of the model for one turn.</summary>
/// <remarks>
///     Implementations are model-free by default (<c>LexicalToolRelevanceSelector</c>); the node may replace the
///     registration with an embedding-backed one, which degrades to the lexical ranking on any failure or timeout.
/// </remarks>
public interface IToolRelevanceSelector
{
    /// <summary>Selects the tools to offer.</summary>
    /// <remarks>
    ///     At or below <paramref name="threshold" /> candidates, or with a <paramref name="query" /> that carries no
    ///     content word, every name is returned and nothing is hidden WITHOUT the ranker being invoked.
    /// </remarks>
    /// <param name="query">The turn's relevance query, the last user message's text; a blank one takes the fast path.</param>
    /// <param name="candidates">Every offered tool, in the order the outbound array carries them.</param>
    /// <param name="threshold">Candidate count above which ranking engages. A trigger, not a cap.</param>
    /// <param name="cancellationToken">Cancels the selection.</param>
    Task<ToolRelevanceSelection> SelectAsync(string? query,
        IReadOnlyList<ToolRelevanceCandidate> candidates,
        int threshold,
        CancellationToken cancellationToken);
}

namespace XE_Local_AI_Engine.Client.Services.Agents;

using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Selects the most relevant subset of already-Enabled playbook actions for a single send, given the incoming
///     user-turn text.
/// </summary>
/// <remarks>
///     The relevance-retrieval seam: the lexical implementation is a deterministic, model-free default, and an
///     embedding-backed ranker drops in behind the same interface. A ranker never widens scope — it only filters and
///     orders the caller-supplied candidate list.
/// </remarks>
public interface IPlaybookRetrievalRanker
{
    /// <summary>
    ///     Returns at most <paramref name="k" /> of <paramref name="candidates" /> judged most relevant to
    ///     <paramref name="query" />, relevance descending, tiebroken on Priority then CreatedAtUtc.
    /// </summary>
    /// <remarks>
    ///     A non-positive <paramref name="k" /> or an empty candidate list yields an empty result, and a blank
    ///     <paramref name="query" /> yields the candidates in priority order, capped to <paramref name="k" />.
    ///     Asynchronous so an embedding-backed ranker can issue a node-local model call; the lexical default
    ///     completes immediately.
    /// </remarks>
    Task<IReadOnlyList<PlaybookActionRecord>> SelectTopKAsync(string query,
        IReadOnlyList<PlaybookActionRecord> candidates,
        int k,
        CancellationToken cancellationToken);
}

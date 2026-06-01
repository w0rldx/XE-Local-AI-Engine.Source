namespace XE_Local_AI_Engine.Client.Services.Agents;

using XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Selects the most relevant subset of already-Enabled playbook actions for a single send, given the incoming
///     user-turn text. This is the Playbook P5 relevance-retrieval seam: the lexical implementation is a
///     deterministic, model-free default, and an embedding-backed ranker can drop in behind the same interface later.
///     The ranker never widens scope — it only filters and orders a caller-supplied candidate list of Enabled actions.
/// </summary>
public interface IPlaybookRetrievalRanker
{
    /// <summary>
    ///     Returns at most <paramref name="k" /> of <paramref name="candidates" /> judged most relevant to
    ///     <paramref name="query" />, ordered by relevance descending with a deterministic tiebreak (Priority ascending,
    ///     then CreatedAtUtc ascending). When <paramref name="k" /> is non-positive or <paramref name="candidates" /> is
    ///     empty the result is empty; when <paramref name="query" /> is blank the candidates are returned in priority
    ///     order, capped to <paramref name="k" />. The method is asynchronous so an embedding-backed ranker can issue a
    ///     node-local model call; the lexical default computes synchronously and completes immediately.
    /// </summary>
    Task<IReadOnlyList<PlaybookActionRecord>> SelectTopKAsync(string query,
        IReadOnlyList<PlaybookActionRecord> candidates,
        int k,
        CancellationToken cancellationToken);
}

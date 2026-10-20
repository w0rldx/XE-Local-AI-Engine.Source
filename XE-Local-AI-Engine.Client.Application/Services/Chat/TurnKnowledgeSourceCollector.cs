namespace XE_Local_AI_Engine.Client.Services.Chat;

using XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Per-turn collector of the knowledge-base hits the agent's search tool returned, flowed as an
///     <see cref="AsyncLocal{T}" /> so the terminal row can persist them as the turn's sources.
/// </summary>
/// <remarks>
///     Same shape as <c>SpawnContext</c> and <c>ToolRelevanceScope</c>: the slot holds a mutable holder, because an
///     <see cref="AsyncLocal{T}" /> write made inside a tool handler never flows back out to the pump. The chat stream
///     and regeneration services seed it before starting the pump and the run, so both inherit the same holder.
/// </remarks>
internal sealed class TurnKnowledgeSourceCollector
{
    /// <summary>Upper bound on the sources persisted for one turn, so several searches cannot bloat the metadata blob.</summary>
    public const int MaxPersistedSources = 20;

    private static readonly AsyncLocal<TurnKnowledgeSourceCollector?> Ambient = new();

    private readonly Lock _gate = new();
    private readonly List<NodeChatMessageSource> _sources = [];
    private readonly HashSet<Guid> _chunkIds = [];

    /// <summary>Seeds a fresh collector for the current flow; disposal restores the prior ambient value.</summary>
    public static IDisposable BeginScope()
    {
        var previous = Ambient.Value;
        Ambient.Value = new TurnKnowledgeSourceCollector();
        return new Scope(previous);
    }

    /// <summary>Records one hit in first-seen order, ignoring a repeated chunk; a no-op when no scope was seeded.</summary>
    public static void Add(KnowledgeSearchHit hit)
    {
        ArgumentNullException.ThrowIfNull(hit);

        if (Ambient.Value is not { } collector)
        {
            return;
        }

        lock (collector._gate)
        {
            if (collector._chunkIds.Add(hit.ChunkId))
            {
                collector._sources.Add(ToSource(hit));
            }
        }
    }

    /// <summary>The hits collected so far in the current flow; empty when no scope was seeded.</summary>
    public static IReadOnlyList<NodeChatMessageSource> Snapshot()
    {
        if (Ambient.Value is not { } collector)
        {
            return [];
        }

        lock (collector._gate)
        {
            return [.. collector._sources];
        }
    }

    /// <summary>
    ///     Pre-turn sources first, then collected ones, deduplicated by chunk id and capped at
    ///     <see cref="MaxPersistedSources" />; <see langword="null" /> when both are empty, which keeps persisted sources.
    /// </summary>
    public static IReadOnlyList<NodeChatMessageSource>? Merge(IReadOnlyList<NodeChatMessageSource>? preTurn, IReadOnlyList<NodeChatMessageSource> collected)
    {
        ArgumentNullException.ThrowIfNull(collected);

        var merged = (preTurn ?? []).Concat(collected).DistinctBy(source => source.ChunkId).Take(MaxPersistedSources).ToList();
        return merged.Count == 0 ? null : merged;
    }

    /// <summary>The persisted provenance of one hit, shared by the plain-chat composer and the search tool.</summary>
    public static NodeChatMessageSource ToSource(KnowledgeSearchHit hit)
    {
        ArgumentNullException.ThrowIfNull(hit);

        return new NodeChatMessageSource(hit.DocumentId, hit.ChunkId, hit.Title, hit.Section, hit.Score, hit.ScoreKind);
    }

    private sealed class Scope : IDisposable
    {
        private readonly TurnKnowledgeSourceCollector? _previous;

        public Scope(TurnKnowledgeSourceCollector? previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            Ambient.Value = _previous;
        }
    }
}

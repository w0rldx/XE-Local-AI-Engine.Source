namespace XE_Local_AI_Engine.Client.Services.DevWorkflows;

using System.Collections.Concurrent;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     The parsed graph for each live run, keyed by run and invalidated by its <c>GraphRevision</c>.
///     <para>
///         No cache library, no eviction policy, no expiry: the entry count is bounded by the concurrent-run cap, one
///         entry per run, replaced when the revision moves and dropped when the run terminalizes. It exists because
///         decrypting and re-parsing the pinned graph on every tick of every run is the one repeated cost the
///         DB-as-truth design would otherwise pay for nothing.
///     </para>
/// </summary>
internal sealed class DevWorkflowGraphCache
{
    private readonly ConcurrentDictionary<Guid, CacheEntry> _entries = new();
    private int _parseCount;

    /// <summary>
    ///     How many times a graph has actually been parsed. Instrumentation, and the only way to assert the invariant
    ///     that no tick keeps advancing against a graph a materialization has since rewritten: the assertion is that the
    ///     next tick re-parses.
    /// </summary>
    public int ParseCount => Volatile.Read(ref _parseCount);

    /// <summary>The run's graph, re-parsed only when the run row's revision has moved past the cached one.</summary>
    public DevWorkflowGraph Resolve(DevWorkflowRunSnapshot run)
    {
        ArgumentNullException.ThrowIfNull(run);

        if (_entries.TryGetValue(run.Id, out var cached) && cached.Revision == run.GraphRevision)
        {
            return cached.Graph;
        }

        var graph = DevWorkflowGraph.Parse(run.GraphJson);
        _ = Interlocked.Increment(ref _parseCount);
        _entries[run.Id] = new CacheEntry(run.GraphRevision, graph);
        return graph;
    }

    /// <summary>Called when a run terminalizes. A forgotten run that turns out to be live again simply re-parses.</summary>
    public void Forget(Guid runId) =>
        _entries.TryRemove(runId, out _);

    private sealed record CacheEntry(int Revision, DevWorkflowGraph Graph);
}

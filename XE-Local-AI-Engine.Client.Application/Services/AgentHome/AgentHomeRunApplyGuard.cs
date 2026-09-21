namespace XE_Local_AI_Engine.Client.Services.AgentHome;

/// <summary>
///     Which runs are having their exported patch applied right now, and the gate every removal of a run directory
///     passes. Node-wide, keyed per run id.
/// </summary>
/// <remarks>
///     An apply reads its patch bytes up front, so a run directory that vanishes mid-apply cannot corrupt what lands
///     in the operator's folders — but the run-log append at the end resolves that directory again and writes
///     NOTHING once it is gone, losing the only record of files that really were written. Not the execution lease:
///     that one is keyed per owner-node SANDBOX, never queues and refuses a poisoned key, so an apply holding it
///     would freeze the sweep and every other run's delete, for a race that is not this one.
/// </remarks>
internal sealed class AgentHomeRunApplyGuard
{
    // A set, not a count: NodePatchApplyService holds a node-wide gate across the whole mutating half, so one run id
    // is never registered twice. If applies ever run concurrently per run, this becomes a counter.
    private readonly HashSet<string> _applying = new(StringComparer.Ordinal);
    private readonly Lock _sync = new();

    /// <summary>Marks <paramref name="runId" /> as being applied until the returned scope is disposed.</summary>
    public IDisposable BeginApply(string runId)
    {
        lock (_sync)
        {
            _ = _applying.Add(runId);
        }

        return new ApplyScope(this, runId);
    }

    /// <summary>
    ///     Runs <paramref name="remove" /> for <paramref name="runId" /> unless an apply of that run is in flight,
    ///     and answers whether it ran.
    /// </summary>
    /// <remarks>
    ///     The removal runs UNDER the lock <see cref="BeginApply" /> also takes, which is what makes the pair atomic:
    ///     an apply can neither register itself against a directory that is half removed nor start on one that is
    ///     about to be. An apply that arrives while a removal holds the lock waits for it and then finds no exported
    ///     patch — the refusal it already answers for a run with nothing to apply.
    /// </remarks>
    public bool TryRemove(string runId, Action remove)
    {
        ArgumentNullException.ThrowIfNull(remove);

        lock (_sync)
        {
            if (_applying.Contains(runId))
            {
                return false;
            }

            remove();
            return true;
        }
    }

    private sealed class ApplyScope : IDisposable
    {
        private readonly AgentHomeRunApplyGuard _owner;
        private string? _runId;

        public ApplyScope(AgentHomeRunApplyGuard owner, string runId)
        {
            _owner = owner;
            _runId = runId;
        }

        public void Dispose()
        {
            // Exchanged first, so a second dispose cannot clear a registration a later apply of the same run made.
            var runId = Interlocked.Exchange(ref _runId, value: null);
            if (runId is null)
            {
                return;
            }

            lock (_owner._sync)
            {
                _ = _owner._applying.Remove(runId);
            }
        }
    }
}

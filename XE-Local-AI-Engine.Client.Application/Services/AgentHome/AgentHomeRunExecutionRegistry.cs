namespace XE_Local_AI_Engine.Client.Services.AgentHome;

/// <summary>
///     Which runs are executing right now, and the gate that tells a finished run's directory from a live one.
///     Node-wide, keyed per run id.
/// </summary>
/// <remarks>
///     Not the execution lease: that one is keyed per owner-node SANDBOX and is taken by an MCP workspace session, a
///     Coder read and a chat attachment re-stage as well, so reading it refuses to delete a run that finished hours
///     ago whenever anything at all is touching the sandbox. In memory and never persisted, which is exactly right:
///     an id is minted inside this process and nothing resumes a run after a restart, so a run directory whose id is
///     not registered here belongs to a run that is over — including one a killed process never finished.
/// </remarks>
internal sealed class AgentHomeRunExecutionRegistry
{
    // A set, not a count: an id is minted once and never re-minted, so one run id is never registered twice.
    private readonly HashSet<string> _executing = new(StringComparer.Ordinal);
    private readonly Lock _sync = new();

    /// <summary>Marks <paramref name="runId" /> as executing until the returned scope is disposed.</summary>
    public IDisposable Begin(string runId)
    {
        lock (_sync)
        {
            _ = _executing.Add(runId);
        }

        return new RunScope(this, runId);
    }

    /// <summary>
    ///     Whether the run with this id is executing right now.
    /// </summary>
    /// <remarks>
    ///     A refusal signal only, like the execution lease's <c>IsHeld</c>: the answer is a snapshot, so "executing"
    ///     must stop a removal while "not executing" is never a licence to assume exclusivity. Registration happens
    ///     before the run directory is created, which is what makes the snapshot safe for a removal — see
    ///     <c>AgentHomeRunDeleteService</c>.
    /// </remarks>
    public bool IsExecuting(string runId)
    {
        lock (_sync)
        {
            return _executing.Contains(runId);
        }
    }

    private sealed class RunScope : IDisposable
    {
        private readonly AgentHomeRunExecutionRegistry _owner;
        private string? _runId;

        public RunScope(AgentHomeRunExecutionRegistry owner, string runId)
        {
            _owner = owner;
            _runId = runId;
        }

        public void Dispose()
        {
            // Exchanged first, so a second dispose cannot clear a registration a later run of the same id made.
            var runId = Interlocked.Exchange(ref _runId, value: null);
            if (runId is null)
            {
                return;
            }

            lock (_owner._sync)
            {
                _ = _owner._executing.Remove(runId);
            }
        }
    }
}

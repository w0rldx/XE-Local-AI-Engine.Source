namespace XE_Local_AI_Engine.Client.Services.Capacity;

/// <summary>
///     Per-root-invocation spawn state, flowed implicitly through the agent tool loop as an <see cref="AsyncLocal{T}" />: the root turn seeds
///     one context at <c>Depth = 0</c> and every <c>spawn_subagent</c> call inside it reads the fan-out and cloud-spawn caps.
/// </summary>
/// <remarks>
///     The PRIMARY recursion cap is STRUCTURAL: a spawned child is built WITHOUT the <c>spawn_subagent</c> tool, so a depth-≥1 agent never
///     reaches this state, and the depth field exists only to give that omission a value to branch on. A missing ambient context defaults
///     SAFE, because with no context no spawn tool is offered. The counters live on the single root context shared by the turn's concurrent
///     spawns; the per-(model, role) semaphore and the byte ledger are process-wide singletons, not here.
/// </remarks>
public sealed class SpawnContext
{
    // The single ambient slot. AsyncLocal flows the value into every continuation the root tool loop awaits, including the IClientLocalToolHandler
    // body the function-invocation pipeline calls, so the handler reads the root's caps without a parameter through MAF's per-invocation-less surface.
    private static readonly AsyncLocal<SpawnContext?> AmbientContext = new();

    private readonly int _cloudSpawnCap;
    private readonly int _fanOutCap;
    private int _cloudSpawnsStarted;
    private int _liveFanOut;

    private SpawnContext(int depth, int fanOutCap, int cloudSpawnCap, string? rootModelId)
    {
        Depth = depth;
        _fanOutCap = fanOutCap;
        _cloudSpawnCap = cloudSpawnCap;
        RootModelId = rootModelId;
    }

    /// <summary>Spawn depth: <c>0</c> is the root tool loop; a spawned child runs at <c>1</c> (and is built tool-less).</summary>
    public int Depth { get; }

    /// <summary>
    ///     The model driving the root tool loop — the PARENT of anything spawned inside this turn — or
    ///     <see langword="null" /> when the seeding caller had none to name.
    /// </summary>
    /// <remarks>
    ///     Carried so the spawn service can refuse a parent that sits outside the node's trust boundary. Spawning is a
    ///     data-egress question, not just a capacity one: a child bound to a node-local model can read the workspace and
    ///     the knowledge base, and its result is returned into the parent's transcript — so a cloud or declared-cloud
    ///     parent that may not be offered those tools directly must not obtain them through a child either.
    /// </remarks>
    public string? RootModelId { get; }

    /// <summary>The ambient context for the current async flow, or <see langword="null" /> when none was seeded (no spawn possible).</summary>
    public static SpawnContext? Current => AmbientContext.Value;

    /// <summary>
    ///     Seeds a fresh root context (<c>Depth = 0</c>) for the current async flow and returns a scope whose disposal restores the prior
    ///     ambient value.
    /// </summary>
    /// <remarks>
    ///     Called once when a root agent tool loop begins; re-entrant seeding is a no-op-safe stack restore, so a nested seed cannot leak a
    ///     child's caps into an outer turn.
    /// </remarks>
    /// <param name="fanOutCap">Maximum concurrent live sub-agents for this turn.</param>
    /// <param name="cloudSpawnCap">Maximum cloud sub-agents started across this turn.</param>
    /// <param name="rootModelId">The model driving the root loop, recorded as <see cref="RootModelId" />.</param>
    public static IDisposable BeginRoot(int fanOutCap, int cloudSpawnCap, string? rootModelId = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fanOutCap);
        ArgumentOutOfRangeException.ThrowIfNegative(cloudSpawnCap);

        var previous = AmbientContext.Value;
        AmbientContext.Value = new SpawnContext(depth: 0, fanOutCap, cloudSpawnCap, rootModelId);
        return new RootScope(previous);
    }

    /// <summary>
    ///     Pushes a child context (<c>Depth = this.Depth + 1</c>) as the ambient value for the inner sub-agent run, and returns a scope that
    ///     restores this (the parent) context on dispose.
    /// </summary>
    /// <remarks>
    ///     The child run executes within this scope so that, should the deliberately tool-less child ever reach the spawn path, the runtime
    ///     depth guard in <see cref="SubAgentSpawnService" /> sees <see cref="Depth" /> ≥ 1 and rejects — defence-in-depth behind the primary
    ///     structural cap. It does NOT re-seed a root, so the parent's AsyncLocal is restored on dispose rather than cleared, and the caps are
    ///     carried forward unused, leaving the per-root fan-out and cloud counters authoritative on the root context.
    /// </remarks>
    public IDisposable BeginChildScope()
    {
        var previous = AmbientContext.Value;
        AmbientContext.Value = new SpawnContext(Depth + 1, _fanOutCap, _cloudSpawnCap, RootModelId);
        return new RootScope(previous);
    }

    /// <summary>Tries to admit one more concurrent live sub-agent against the fan-out cap.</summary>
    /// <remarks>
    ///     On success returns a non-null handle that decrements the live count on dispose (wrap the child run in a <c>using</c>); at the cap
    ///     it returns <see langword="null" /> and the caller rejects the spawn. Atomic compare-and-increment, so concurrent spawns in the same
    ///     turn cannot both pass the last slot.
    /// </remarks>
    public IDisposable? TryEnterFanOut()
    {
        while (true)
        {
            var current = Volatile.Read(ref _liveFanOut);
            if (current >= _fanOutCap)
            {
                return null;
            }

            if (Interlocked.CompareExchange(ref _liveFanOut, current + 1, current) == current)
            {
                return new FanOutLease(this);
            }
        }
    }

    /// <summary>Tries to consume one cloud-spawn budget unit against the cloud-spawn cap.</summary>
    /// <remarks>
    ///     Cloud spawns are counted for the whole turn and never decremented on exit, because the cap bounds total paid spend per turn rather
    ///     than concurrency. A non-positive cap rejects every cloud spawn.
    /// </remarks>
    public bool TryConsumeCloudSpawn()
    {
        while (true)
        {
            var current = Volatile.Read(ref _cloudSpawnsStarted);
            if (current >= _cloudSpawnCap)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _cloudSpawnsStarted, current + 1, current) == current)
            {
                return true;
            }
        }
    }

    private void ExitFanOut()
    {
        Interlocked.Decrement(ref _liveFanOut);
    }

    // Restores the prior ambient SpawnContext when disposed, so a nested root seed cannot leak its caps into an outer
    // turn. Idempotent: a double-dispose simply re-restores the same prior value.
    private sealed class RootScope : IDisposable
    {
        private readonly SpawnContext? _previous;

        public RootScope(SpawnContext? previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            AmbientContext.Value = _previous;
        }
    }

    private sealed class FanOutLease : IDisposable
    {
        private readonly SpawnContext _context;
        private int _released;

        public FanOutLease(SpawnContext context)
        {
            _context = context;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                _context.ExitFanOut();
            }
        }
    }
}

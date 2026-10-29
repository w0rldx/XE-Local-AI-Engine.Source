namespace XE_Local_AI_Engine.Providers.Abstractions;

using System.Diagnostics.CodeAnalysis;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;

/// <summary>Lock-protected atomic implementation of a native runtime's activity gate.</summary>
/// <remarks>
///     Every granted lease and every release raises the residency tick, outside the lock, once the snapshot shows it.
///     Each runtime derives its own sealed gate so DI holds one independent instance per runtime.
/// </remarks>
public abstract class RuntimeActivityGate : IRuntimeActivityGate
{
    private readonly Lock _gate = new();
    private readonly IRuntimeResidencyChangeNotifier _residencyNotifier;
    private int _activeJobs;
    private bool _evictionReserved;
    private bool _mutationReserved;
    private int _residentProcesses;
    private int _spawnReadiness;

    protected RuntimeActivityGate(IRuntimeResidencyChangeNotifier? residencyNotifier)
    {
        _residencyNotifier = residencyNotifier ?? NullRuntimeResidencyChangeNotifier.Instance;
    }

    /// <inheritdoc />
    public RuntimeActivitySnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return SnapshotUnderLock();
        }
    }

    /// <inheritdoc />
    public bool TryAcquireJobLease([NotNullWhen(true)] out IRuntimeActivityLease? lease)
    {
        lock (_gate)
        {
            if (_mutationReserved || _evictionReserved)
            {
                lease = null;
                return false;
            }

            _activeJobs++;
        }

        lease = Granted(LeaseKind.Job);
        return true;
    }

    /// <inheritdoc />
    public bool TryAcquireSpawnReadinessLease([NotNullWhen(true)] out IRuntimeActivityLease? lease)
    {
        lock (_gate)
        {
            if (_mutationReserved || _evictionReserved)
            {
                lease = null;
                return false;
            }

            _spawnReadiness++;
        }

        lease = Granted(LeaseKind.SpawnReadiness);
        return true;
    }

    /// <inheritdoc />
    public bool TryAcquireResidentProcessLease([NotNullWhen(true)] out IRuntimeActivityLease? lease)
    {
        lock (_gate)
        {
            if (_mutationReserved || _evictionReserved)
            {
                lease = null;
                return false;
            }

            _residentProcesses++;
        }

        lease = Granted(LeaseKind.ResidentProcess);
        return true;
    }

    /// <inheritdoc />
    public bool TryAcquireEvictionReservation([NotNullWhen(true)] out IRuntimeActivityLease? lease)
    {
        lock (_gate)
        {
            // An eject may tear down a RESIDENT process — that is its whole point — but never one that is mid-flight
            // or mid-spawn.
            if (_mutationReserved || _evictionReserved || _activeJobs != 0 || _spawnReadiness != 0)
            {
                lease = null;
                return false;
            }

            _evictionReserved = true;
        }

        lease = Granted(LeaseKind.Eviction);
        return true;
    }

    /// <inheritdoc />
    public bool TryAcquireMutationReservation([NotNullWhen(true)] out IRuntimeActivityLease? lease)
    {
        lock (_gate)
        {
            // A managed-runtime mutation replaces the bytes on disk, so it additionally requires that no process is
            // resident: a running child still has the binary and the model file open.
            if (_mutationReserved || _evictionReserved || _activeJobs != 0 || _spawnReadiness != 0 || _residentProcesses != 0)
            {
                lease = null;
                return false;
            }

            _mutationReserved = true;
        }

        lease = Granted(LeaseKind.Mutation);
        return true;
    }

    private Lease Granted(LeaseKind kind)
    {
        _residencyNotifier.NotifyChanged();
        return new Lease(this, kind);
    }

    private RuntimeActivitySnapshot SnapshotUnderLock()
    {
        return new RuntimeActivitySnapshot
        {
            ActiveJobCount = _activeJobs,
            SpawnReadinessCount = _spawnReadiness,
            ResidentProcessCount = _residentProcesses,
            MutationReserved = _mutationReserved,
            EvictionReserved = _evictionReserved
        };
    }

    private void Release(LeaseKind kind)
    {
        lock (_gate)
        {
            switch (kind)
            {
                case LeaseKind.Job:
                    _activeJobs--;
                    break;
                case LeaseKind.SpawnReadiness:
                    _spawnReadiness--;
                    break;
                case LeaseKind.ResidentProcess:
                    _residentProcesses--;
                    break;
                case LeaseKind.Mutation:
                    _mutationReserved = false;
                    break;
                case LeaseKind.Eviction:
                    _evictionReserved = false;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
            }
        }

        _residencyNotifier.NotifyChanged();
    }

    private enum LeaseKind
    {
        Job,
        SpawnReadiness,
        ResidentProcess,
        Mutation,
        Eviction
    }

    private sealed class Lease : IRuntimeActivityLease
    {
        private readonly RuntimeActivityGate _owner;
        private readonly LeaseKind _kind;
        private int _disposed;

        public Lease(RuntimeActivityGate owner, LeaseKind kind)
        {
            _owner = owner;
            _kind = kind;
        }

        public void Dispose() =>
            ReleaseOnce();

        public ValueTask DisposeAsync()
        {
            ReleaseOnce();
            return ValueTask.CompletedTask;
        }

        // Shared by both disposal shapes; there is nothing to await, so DisposeAsync releases through this rather than through Dispose(). Exactly once: a double dispose must not decrement a counter
        // twice and let a mutation in while work is still in flight.
        private void ReleaseOnce()
        {
            if (Interlocked.Exchange(ref _disposed, value: 1) == 0)
            {
                _owner.Release(_kind);
            }
        }
    }
}

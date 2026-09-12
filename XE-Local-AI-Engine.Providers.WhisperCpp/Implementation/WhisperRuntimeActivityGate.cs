namespace XE_Local_AI_Engine.Providers.WhisperCpp.Implementation;

using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;

/// <summary>Lock-protected atomic implementation of the transcription-runtime activity gate.</summary>
public sealed class WhisperRuntimeActivityGate : IWhisperRuntimeActivityGate
{
    private readonly Lock _gate = new();
    private int _activeTranscriptions;
    private bool _evictionReserved;
    private bool _mutationReserved;
    private int _residentProcesses;
    private int _spawnReadiness;

    /// <inheritdoc />
    public WhisperRuntimeActivitySnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return SnapshotUnderLock();
        }
    }

    /// <inheritdoc />
    public IWhisperRuntimeActivityLease? TryAcquireTranscriptionLease()
    {
        lock (_gate)
        {
            if (_mutationReserved || _evictionReserved)
            {
                return null;
            }

            _activeTranscriptions++;
            return new Lease(this, LeaseKind.Transcription);
        }
    }

    /// <inheritdoc />
    public IWhisperRuntimeActivityLease? TryAcquireSpawnReadinessLease()
    {
        lock (_gate)
        {
            if (_mutationReserved || _evictionReserved)
            {
                return null;
            }

            _spawnReadiness++;
            return new Lease(this, LeaseKind.SpawnReadiness);
        }
    }

    /// <inheritdoc />
    public IWhisperRuntimeActivityLease? TryAcquireResidentProcessLease()
    {
        lock (_gate)
        {
            if (_mutationReserved || _evictionReserved)
            {
                return null;
            }

            _residentProcesses++;
            return new Lease(this, LeaseKind.ResidentProcess);
        }
    }

    /// <inheritdoc />
    public IWhisperRuntimeActivityLease? TryAcquireEvictionReservation()
    {
        lock (_gate)
        {
            // An eject may tear down a RESIDENT process — that is its whole point — but never one that is mid-flight
            // or mid-spawn.
            if (_mutationReserved || _evictionReserved || _activeTranscriptions != 0 || _spawnReadiness != 0)
            {
                return null;
            }

            _evictionReserved = true;
            return new Lease(this, LeaseKind.Eviction);
        }
    }

    /// <inheritdoc />
    public IWhisperRuntimeActivityLease? TryAcquireMutationReservation()
    {
        lock (_gate)
        {
            // A managed-runtime mutation replaces the bytes on disk, so it additionally requires that no process is
            // resident: a running child still has the binary and the model file open.
            if (_mutationReserved || _evictionReserved || _activeTranscriptions != 0 || _spawnReadiness != 0 || _residentProcesses != 0)
            {
                return null;
            }

            _mutationReserved = true;
            return new Lease(this, LeaseKind.Mutation);
        }
    }

    private WhisperRuntimeActivitySnapshot SnapshotUnderLock()
    {
        return new WhisperRuntimeActivitySnapshot(_activeTranscriptions,
            _spawnReadiness,
            _residentProcesses,
            _mutationReserved,
            _evictionReserved);
    }

    private void Release(LeaseKind kind)
    {
        lock (_gate)
        {
            switch (kind)
            {
                case LeaseKind.Transcription:
                    _activeTranscriptions--;
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
    }

    private enum LeaseKind
    {
        Transcription,
        SpawnReadiness,
        ResidentProcess,
        Mutation,
        Eviction
    }

    private sealed class Lease(WhisperRuntimeActivityGate owner, LeaseKind kind) : IWhisperRuntimeActivityLease
    {
        private int _disposed;

        public void Dispose()
        {
            // Exactly once: a double dispose must not decrement a counter twice and let a mutation in while work is
            // still in flight.
            if (Interlocked.Exchange(ref _disposed, value: 1) == 0)
            {
                owner.Release(kind);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

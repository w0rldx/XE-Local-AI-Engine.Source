namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;

using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

/// <summary>Lock-protected atomic implementation of the image-runtime activity gate.</summary>
public sealed class ImageRuntimeActivityGate : IImageRuntimeActivityGate
{
    private readonly Lock _gate = new();
    private int _activeJobs;
    private bool _evictionReserved;
    private bool _mutationReserved;
    private int _residentProcesses;
    private int _spawnReadiness;

    public ImageRuntimeActivitySnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return SnapshotUnderLock();
        }
    }

    public IImageRuntimeActivityLease? TryAcquireJobLease()
    {
        lock (_gate)
        {
            if (_mutationReserved || _evictionReserved)
            {
                return null;
            }

            _activeJobs++;
            return new Lease(this, LeaseKind.Job);
        }
    }

    public IImageRuntimeActivityLease? TryAcquireSpawnReadinessLease()
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

    public IImageRuntimeActivityLease? TryAcquireResidentProcessLease()
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

    public IImageRuntimeActivityLease? TryAcquireEvictionReservation()
    {
        lock (_gate)
        {
            if (_mutationReserved || _evictionReserved || _activeJobs != 0 || _spawnReadiness != 0)
            {
                return null;
            }

            _evictionReserved = true;
            return new Lease(this, LeaseKind.Eviction);
        }
    }

    public IImageRuntimeActivityLease? TryAcquireMutationReservation()
    {
        lock (_gate)
        {
            if (_mutationReserved || _evictionReserved || _activeJobs != 0 || _spawnReadiness != 0 || _residentProcesses != 0)
            {
                return null;
            }

            _mutationReserved = true;
            return new Lease(this, LeaseKind.Mutation);
        }
    }

    private ImageRuntimeActivitySnapshot SnapshotUnderLock()
    {
        return new ImageRuntimeActivitySnapshot(_activeJobs, _spawnReadiness, _residentProcesses, _mutationReserved, _evictionReserved);
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
    }

    private enum LeaseKind
    {
        Job,
        SpawnReadiness,
        ResidentProcess,
        Mutation,
        Eviction
    }

    private sealed class Lease(ImageRuntimeActivityGate owner, LeaseKind kind) : IImageRuntimeActivityLease
    {
        private int _disposed;

        public void Dispose()
        {
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

namespace XE_Local_AI_Engine.Client.Services.Capacity.Implementation;

/// <inheritdoc />
public sealed class GpuWorkGate : IGpuWorkGate
{
    private readonly Lock _gate = new();
    private readonly List<GpuWorkKind> _shared = [];

    private GpuWorkKind? _exclusive;

    public GpuWorkKind? ExclusiveKind
    {
        get
        {
            lock (_gate)
            {
                return _exclusive;
            }
        }
    }

    public IDisposable? TryBeginExclusive(GpuWorkKind kind)
    {
        lock (_gate)
        {
            if (_exclusive is not null || _shared.Count > 0)
            {
                return null;
            }

            _exclusive = kind;
            return new Handle(this, kind, exclusive: true);
        }
    }

    public IDisposable? TryBeginShared(GpuWorkKind kind)
    {
        lock (_gate)
        {
            if (_exclusive is not null)
            {
                return null;
            }

            _shared.Add(kind);
            return new Handle(this, kind, exclusive: false);
        }
    }

    private void Release(GpuWorkKind kind, bool exclusive)
    {
        lock (_gate)
        {
            if (exclusive)
            {
                _exclusive = null;
                return;
            }

            _ = _shared.Remove(kind);
        }
    }

    private sealed class Handle : IDisposable
    {
        private readonly GpuWorkKind _kind;
        private readonly bool _exclusive;
        private GpuWorkGate? _owner;

        public Handle(GpuWorkGate owner, GpuWorkKind kind, bool exclusive)
        {
            _kind = kind;
            _exclusive = exclusive;
            _owner = owner;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _owner, null) is { } released)
            {
                released.Release(_kind, _exclusive);
            }
        }
    }
}

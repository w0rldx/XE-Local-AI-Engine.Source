namespace XE_Local_AI_Engine.Client.Services.Capacity.Implementation;

using System.Diagnostics.CodeAnalysis;

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

    public IReadOnlyList<GpuWorkKind> Holders
    {
        get
        {
            lock (_gate)
            {
                return _exclusive is { } exclusive ? [exclusive, .. _shared] : [.. _shared];
            }
        }
    }

    public bool TryBeginExclusive(GpuWorkKind kind, [NotNullWhen(true)] out IDisposable? handle)
    {
        lock (_gate)
        {
            if (_exclusive is not null || _shared.Count > 0)
            {
                handle = null;
                return false;
            }

            _exclusive = kind;
            handle = new Handle(this, kind, exclusive: true);
            return true;
        }
    }

    public bool TryBeginShared(GpuWorkKind kind, [NotNullWhen(true)] out IDisposable? handle)
    {
        lock (_gate)
        {
            if (_exclusive is not null)
            {
                handle = null;
                return false;
            }

            _shared.Add(kind);
            handle = new Handle(this, kind, exclusive: false);
            return true;
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

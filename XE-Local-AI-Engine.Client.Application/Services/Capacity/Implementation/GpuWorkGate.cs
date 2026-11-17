namespace XE_Local_AI_Engine.Client.Services.Capacity.Implementation;

using System.Diagnostics.CodeAnalysis;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;

/// <inheritdoc />
/// <remarks>A refusal is GPU demand: it asks an idle holder to leave, after the lock, so a later attempt can admit.</remarks>
public sealed class GpuWorkGate : IGpuWorkGate
{
    private readonly Lock _gate = new();
    private readonly IGpuIdleHolderPreemption _idlePreemption;
    private readonly List<GpuWorkKind> _shared = [];

    private GpuWorkKind? _exclusive;

    /// <param name="idlePreemption">Told of every refusal; null (no idle holder can exist) raises nothing.</param>
    public GpuWorkGate(IGpuIdleHolderPreemption? idlePreemption = null)
    {
        _idlePreemption = idlePreemption ?? NoOpGpuIdleHolderPreemption.Instance;
    }

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
            if (_exclusive is null && _shared.Count == 0)
            {
                _exclusive = kind;
                handle = new Handle(this, kind, exclusive: true);
                return true;
            }
        }

        handle = null;
        _idlePreemption.RequestPreemption();
        return false;
    }

    public bool TryBeginShared(GpuWorkKind kind, [NotNullWhen(true)] out IDisposable? handle)
    {
        lock (_gate)
        {
            if (_exclusive is null)
            {
                _shared.Add(kind);
                handle = new Handle(this, kind, exclusive: false);
                return true;
            }
        }

        handle = null;
        _idlePreemption.RequestPreemption();
        return false;
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

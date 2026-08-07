namespace XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;

using System.Collections.Concurrent;

internal sealed class AgentHomeExecutionLeaseManager : IAgentHomeExecutionLeaseManager
{
    private readonly AsyncLocal<LeaseScope?> _ambientScope = new();
    private readonly ConcurrentDictionary<AgentHomeExecutionLeaseKey, SemaphoreSlim> _gates = new();
    private readonly ConcurrentDictionary<AgentHomeExecutionLeaseKey, byte> _poisoned = new();

    public IAgentHomeExecutionLease? TryAcquire(AgentHomeExecutionLeaseKey key)
    {
        return TryAcquireCore(key, allowPoisoned: false);
    }

    public IAgentHomeExecutionLease? TryAcquireForRecovery(AgentHomeExecutionLeaseKey key)
    {
        return TryAcquireCore(key, allowPoisoned: true);
    }

    public bool IsPoisoned(AgentHomeExecutionLeaseKey key) => _poisoned.ContainsKey(key);

    public void MarkPoisoned(AgentHomeExecutionLeaseKey key) => _poisoned[key] = 0;

    public void ClearPoison(AgentHomeExecutionLeaseKey key) => _ = _poisoned.TryRemove(key, out _);

    private IAgentHomeExecutionLease? TryAcquireCore(AgentHomeExecutionLeaseKey key, bool allowPoisoned)
    {
        if (string.IsNullOrWhiteSpace(key.OwnerUserId) || string.IsNullOrWhiteSpace(key.NodeId))
        {
            throw new ArgumentException("The execution lease key requires an owner and node.", nameof(key));
        }

        var ambient = NormalizeAmbient();
        if (!allowPoisoned && IsPoisoned(key))
        {
            return null;
        }

        if (ambient is not null)
        {
            return ambient.Key == key ? BorrowedLease.Instance : null;
        }

        var gate = _gates.GetOrAdd(key, static _ => new SemaphoreSlim(initialCount: 1, maxCount: 1));
        if (!gate.Wait(millisecondsTimeout: 0))
        {
            return null;
        }

        var authority = new LeaseAuthority();
        var scope = new LeaseScope(key, authority, prior: null);
        _ambientScope.Value = scope;
        return new OwnedLease(this, gate, scope, authority);
    }

    private LeaseScope? NormalizeAmbient()
    {
        var current = _ambientScope.Value;
        while (current is not null && (!current.IsActive || current.Authority.IsDisposed))
        {
            current = current.Prior;
        }

        _ambientScope.Value = current;
        return current;
    }

    private sealed class LeaseAuthority
    {
        private int _disposed;

        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public void Revoke() => _ = Interlocked.Exchange(ref _disposed, 1);
    }

    private sealed class LeaseScope
    {
        public LeaseScope(AgentHomeExecutionLeaseKey key, LeaseAuthority authority, LeaseScope? prior)
        {
            Key = key;
            Authority = authority;
            Prior = prior;
        }

        public AgentHomeExecutionLeaseKey Key { get; }

        public LeaseAuthority Authority { get; }

        public LeaseScope? Prior { get; }

        public bool IsActive { get; set; } = true;
    }

    private sealed class OwnedLease : IAgentHomeExecutionLease
    {
        private readonly SemaphoreSlim _gate;
        private readonly AgentHomeExecutionLeaseKey _key;
        private readonly AgentHomeExecutionLeaseManager _owner;
        private readonly LeaseAuthority _authority;
        private LeaseScope? _scope;

        public OwnedLease(AgentHomeExecutionLeaseManager owner, SemaphoreSlim gate, LeaseScope scope, LeaseAuthority authority)
        {
            _owner = owner;
            _gate = gate;
            _key = scope.Key;
            _scope = scope;
            _authority = authority;
        }

        public bool IsBorrowed => false;

        public IDisposable EnterAmbientScope()
        {
            ObjectDisposedException.ThrowIf(_scope is null, this);

            var scope = new LeaseScope(_key, _authority, _owner.NormalizeAmbient());
            _owner._ambientScope.Value = scope;
            return new AmbientActivation(_owner, scope);
        }

        public void Dispose()
        {
            var scope = Interlocked.Exchange(ref _scope, null);
            if (scope is null)
            {
                return;
            }

            _authority.Revoke();
            scope.IsActive = false;
            if (ReferenceEquals(_owner._ambientScope.Value, scope))
            {
                _ = _owner.NormalizeAmbient();
            }

            _gate.Release();
        }
    }

    private sealed class BorrowedLease : IAgentHomeExecutionLease
    {
        public static BorrowedLease Instance { get; } = new();

        public bool IsBorrowed => true;

        public IDisposable EnterAmbientScope()
        {
            return this;
        }

        public void Dispose()
        {
        }
    }

    private sealed class AmbientActivation : IDisposable
    {
        private readonly AgentHomeExecutionLeaseManager _owner;
        private LeaseScope? _scope;

        public AmbientActivation(AgentHomeExecutionLeaseManager owner, LeaseScope scope)
        {
            _owner = owner;
            _scope = scope;
        }

        public void Dispose()
        {
            var scope = Interlocked.Exchange(ref _scope, null);
            if (scope is null)
            {
                return;
            }

            scope.IsActive = false;
            if (ReferenceEquals(_owner._ambientScope.Value, scope))
            {
                _ = _owner.NormalizeAmbient();
            }
        }
    }
}

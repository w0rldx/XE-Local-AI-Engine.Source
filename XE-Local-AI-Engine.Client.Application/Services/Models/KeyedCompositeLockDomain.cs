namespace XE_Local_AI_Engine.Client.Services.Models;

using System.Collections.ObjectModel;

public enum ModelCoordinationLockMode
{
    Read,
    Mutation
}

public sealed class KeyedCompositeLockDomain
{
    private static readonly AsyncLocal<FlowState?> CurrentFlow = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, KeyState> _keys = new(StringComparer.Ordinal);
    private readonly LinkedList<Waiter> _waiters = new();

    public ValueTask<ModelCoordinationLockLease> AcquireReadAsync(IEnumerable<string> keys, CancellationToken cancellationToken = default) =>
        AcquireAsync(keys, ModelCoordinationLockMode.Read, cancellationToken);

    public ValueTask<ModelCoordinationLockLease> AcquireMutationAsync(IEnumerable<string> keys, CancellationToken cancellationToken = default) =>
        AcquireAsync(keys, ModelCoordinationLockMode.Mutation, cancellationToken);

    private ValueTask<ModelCoordinationLockLease> AcquireAsync(IEnumerable<string> keys,
        ModelCoordinationLockMode mode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var flow = CurrentFlow.Value ??= new FlowState();
        if (flow.HeldDomains.Contains(this))
        {
            throw new InvalidOperationException("Nested or re-entrant model coordination acquisition is not permitted.");
        }

        var normalizedKeys = ModelCoordinationKeys.NormalizeSet(keys);
        lock (_gate)
        {
            if (_waiters.Count == 0 && CanGrant(normalizedKeys, mode))
            {
                Grant(normalizedKeys, mode);
                MarkHeld(flow);
                return ValueTask.FromResult(CreateLease(normalizedKeys, mode));
            }

            var waiter = new Waiter(normalizedKeys, mode);
            waiter.Node = _waiters.AddLast(waiter);
            if (cancellationToken.CanBeCanceled)
            {
                waiter.CancellationRegistration = cancellationToken.Register(static state =>
                {
                    var registration = (CancellationState)state!;
                    registration.Domain.Cancel(registration.Waiter, registration.Token);
                }, new CancellationState(this, waiter, cancellationToken));
            }

            return AwaitWaiterAsync(waiter, flow);
        }
    }

    private async ValueTask<ModelCoordinationLockLease> AwaitWaiterAsync(Waiter waiter, FlowState flow)
    {
        try
        {
            var lease = await waiter.Completion.Task.ConfigureAwait(false);
            MarkHeld(flow);
            return lease;
        }
        finally
        {
            waiter.CancellationRegistration.Dispose();
        }
    }

    private void Cancel(Waiter waiter, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (waiter.Node?.List is null)
            {
                return;
            }

            _waiters.Remove(waiter.Node);
            waiter.Node = null;
            _ = waiter.Completion.TrySetCanceled(cancellationToken);
            GrantWaiters();
        }
    }

    private bool CanGrant(IReadOnlyList<string> keys, ModelCoordinationLockMode mode)
    {
        foreach (var key in keys)
        {
            if (!_keys.TryGetValue(key, out var state))
            {
                continue;
            }

            if (state.Writer || (mode == ModelCoordinationLockMode.Mutation && state.Readers > 0))
            {
                return false;
            }
        }

        return true;
    }

    private void Grant(IReadOnlyList<string> keys, ModelCoordinationLockMode mode)
    {
        foreach (var key in keys)
        {
            if (!_keys.TryGetValue(key, out var state))
            {
                state = new KeyState();
                _keys.Add(key, state);
            }

            if (mode == ModelCoordinationLockMode.Read)
            {
                state.Readers++;
            }
            else
            {
                state.Writer = true;
            }
        }
    }

    private ModelCoordinationLockLease CreateLease(IReadOnlyList<string> keys, ModelCoordinationLockMode mode)
    {
        return new ModelCoordinationLockLease(this, new ReadOnlyCollection<string>(keys.ToArray()), mode);
    }

    private void MarkHeld(FlowState flow)
    {
        if (!flow.HeldDomains.Add(this))
        {
            throw new InvalidOperationException("Nested or re-entrant model coordination acquisition is not permitted.");
        }
    }

    internal void Release(IReadOnlyList<string> keys, ModelCoordinationLockMode mode)
    {
        lock (_gate)
        {
            foreach (var key in keys)
            {
                var state = _keys[key];
                if (mode == ModelCoordinationLockMode.Read)
                {
                    state.Readers--;
                }
                else
                {
                    state.Writer = false;
                }

                if (state.Readers == 0 && !state.Writer)
                {
                    _keys.Remove(key);
                }
            }

            CurrentFlow.Value?.HeldDomains.Remove(this);
            GrantWaiters();
        }
    }

    private void GrantWaiters()
    {
        while (_waiters.First is { } node)
        {
            var waiter = node.Value;
            if (!CanGrant(waiter.Keys, waiter.Mode))
            {
                return;
            }

            _waiters.RemoveFirst();
            waiter.Node = null;
            Grant(waiter.Keys, waiter.Mode);
            _ = waiter.Completion.TrySetResult(CreateLease(waiter.Keys, waiter.Mode));
        }
    }

    private sealed class KeyState
    {
        public int Readers { get; set; }
        public bool Writer { get; set; }
    }

    private sealed class FlowState
    {
        public HashSet<KeyedCompositeLockDomain> HeldDomains { get; } = [];
    }

    private sealed class Waiter(IReadOnlyList<string> keys, ModelCoordinationLockMode mode)
    {
        public IReadOnlyList<string> Keys { get; } = keys;
        public ModelCoordinationLockMode Mode { get; } = mode;
        public TaskCompletionSource<ModelCoordinationLockLease> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public LinkedListNode<Waiter>? Node { get; set; }
        public CancellationTokenRegistration CancellationRegistration { get; set; }
    }

    private sealed record CancellationState(KeyedCompositeLockDomain Domain, Waiter Waiter, CancellationToken Token);
}

public sealed class ModelCoordinationLockLease : IAsyncDisposable
{
    private KeyedCompositeLockDomain? _domain;

    internal ModelCoordinationLockLease(KeyedCompositeLockDomain domain,
        IReadOnlyList<string> keys,
        ModelCoordinationLockMode mode)
    {
        _domain = domain;
        Keys = keys;
        Mode = mode;
    }

    public IReadOnlyList<string> Keys { get; }
    public ModelCoordinationLockMode Mode { get; }
    public bool IsDisposed => _domain is null;

    public ValueTask DisposeAsync()
    {
        var domain = Interlocked.Exchange(ref _domain, null);
        domain?.Release(Keys, Mode);
        return ValueTask.CompletedTask;
    }
}

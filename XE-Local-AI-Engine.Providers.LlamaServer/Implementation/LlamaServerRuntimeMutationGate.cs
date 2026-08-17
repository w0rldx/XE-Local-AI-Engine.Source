namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Orders ordinary ensures against the rare operator runtime MUTATION (runtime install/remove, source build,
///     exclusive profiling) for <see cref="LlamaServerProcessSupervisor" />. Ensures take it SHARED and proceed
///     concurrently; a mutation takes it EXCLUSIVE. What an exclusive holder relies on is unchanged from the single
///     semaphore this replaces — a mutation waits for every in-flight ensure DECISION, and no new decision starts
///     while it holds the gate — but an ensure no longer head-of-line blocks an unrelated role behind its liveness
///     probe (up to <c>ReuseLivenessProbeTimeout</c>, 2 s). Single-flight per process is NOT this gate's job: the
///     supervisor's per-(model, role) ensure gates already provide it.
/// </summary>
/// <remarks>
///     <para>
///         Two independent counters live here. The shared/exclusive <see cref="AsyncSharedExclusiveGate" /> orders
///         ensures against mutations. The separate OPERATION barrier
///         (<see cref="BeginOperation" />/<see cref="EndOperation" />) spans a whole public supervisor call, gate
///         entries included, so teardown can prove every admitted operation has finished before it disposes anything.
///     </para>
///     <para>
///         <paramref name="ownerType" /> is the type reported by every <see cref="ObjectDisposedException" /> this
///         gate throws: callers see the supervisor's name, not this internal helper's.
///     </para>
/// </remarks>
internal sealed class LlamaServerRuntimeMutationGate(Type ownerType, CancellationToken shutdownToken) : IDisposable
{
    private readonly AsyncSharedExclusiveGate _gate = new();
    private readonly Type _ownerType = ownerType ?? throw new ArgumentNullException(nameof(ownerType));
    private readonly Lock _operationSync = new();
    private int _disposed;
    private int _mutationActivityCount;
    private TaskCompletionSource? _operationsDrained;
    private int _operationCount;

    /// <summary>Whether an operator runtime mutation is currently in flight (keep-warm stays suppressed while it is).</summary>
    public bool IsMutationActive => Volatile.Read(ref _mutationActivityCount) > 0;

    public void Dispose()
    {
        _gate.Dispose();
    }

    /// <summary>
    ///     Latches the disposed flag under the operation lock, so no operation admitted after this point can start.
    ///     Returns <see langword="false" /> when teardown already ran.
    /// </summary>
    public bool TryMarkDisposed()
    {
        lock (_operationSync)
        {
            if (_disposed != 0)
            {
                return false;
            }

            _disposed = 1;
            return true;
        }
    }

    public void BeginOperation()
    {
        lock (_operationSync)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, _ownerType);
            if (_operationCount++ == 0)
            {
                _operationsDrained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }

    public void EndOperation()
    {
        TaskCompletionSource? drained = null;
        lock (_operationSync)
        {
            _operationCount--;
            if (_operationCount == 0)
            {
                drained = _operationsDrained;
                _operationsDrained = null;
            }
        }

        drained?.TrySetResult();
    }

    public Task WaitForOperationsDrainedAsync()
    {
        lock (_operationSync)
        {
            return _operationCount == 0 ? Task.CompletedTask : _operationsDrained!.Task;
        }
    }

    /// <summary>
    ///     Enters the runtime gate SHARED for an ordinary ensure: concurrent with other ensures, excluded by (and
    ///     excluding) an operator runtime mutation. Pairs with <see cref="ExitShared" />.
    /// </summary>
    public Task EnterSharedAsync(CancellationToken ct)
    {
        return EnterAsync(shared: true, ct);
    }

    /// <summary>
    ///     Enters the runtime gate EXCLUSIVE for an operator runtime mutation or an exclusive profiling spawn: waits
    ///     for every in-flight ensure decision to finish and holds off every new one. Pairs with
    ///     <see cref="ExitExclusive" />.
    /// </summary>
    public Task EnterExclusiveAsync(CancellationToken ct)
    {
        return EnterAsync(shared: false, ct);
    }

    /// <summary>
    ///     Owns the gate exclusively through teardown, bypassing the disposed check that every ordinary entry makes.
    ///     Valid only after <see cref="TryMarkDisposed" /> latched and <see cref="WaitForOperationsDrainedAsync" />
    ///     proved every admitted operation has finished.
    /// </summary>
    public Task EnterExclusiveForTeardownAsync()
    {
        return _gate.EnterExclusiveAsync(CancellationToken.None);
    }

    public void ExitShared()
    {
        _gate.ExitShared();
    }

    public void ExitExclusive()
    {
        _gate.ExitExclusive();
    }

    /// <summary>
    ///     Takes the gate EXCLUSIVE on behalf of an operator runtime mutation and hands back the lease that holds it.
    ///     <paramref name="mutationBlocked" /> is evaluated under the gate: when it reports live or in-flight
    ///     processes the gate is released again and no lease is issued, because a runtime swap under a loaded model
    ///     would pull the binaries out from under it.
    /// </summary>
    public async Task<ILlamaServerRuntimeMutationLease?> TryAcquireLeaseAsync(Func<bool> mutationBlocked, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(mutationBlocked);
        BeginOperation();
        var operationTransferred = false;
        Interlocked.Increment(ref _mutationActivityCount);
        try
        {
            // EXCLUSIVE: the mutation about to run replaces the runtime binaries under the supervisor, so it must see a
            // quiet supervisor — every in-flight ensure decision has finished and no new one can start.
            await EnterExclusiveAsync(ct).ConfigureAwait(false);

            if (mutationBlocked())
            {
                ExitExclusive();
                return null;
            }

            var lease = new RuntimeMutationLease(_gate,
                () =>
                {
                    Interlocked.Decrement(ref _mutationActivityCount);
                    EndOperation();
                });
            operationTransferred = true;
            return lease;
        }
        finally
        {
            if (!operationTransferred)
            {
                Interlocked.Decrement(ref _mutationActivityCount);
                EndOperation();
            }
        }
    }

    private async Task EnterAsync(bool shared, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, _ownerType);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, shutdownToken);
        try
        {
            var entering = shared
                ? _gate.EnterSharedAsync(linkedCancellation.Token)
                : _gate.EnterExclusiveAsync(linkedCancellation.Token);
            await entering.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ObjectDisposedException(_ownerType.FullName);
        }

        if (Volatile.Read(ref _disposed) == 0)
        {
            return;
        }

        if (shared)
        {
            _gate.ExitShared();
        }
        else
        {
            _gate.ExitExclusive();
        }

        throw new ObjectDisposedException(_ownerType.FullName);
    }

    private sealed class RuntimeMutationLease(AsyncSharedExclusiveGate gate, Action onDisposed) : ILlamaServerRuntimeMutationLease
    {
        private int _disposed;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                gate.ExitExclusive();
                onDisposed();
            }

            return ValueTask.CompletedTask;
        }
    }
}

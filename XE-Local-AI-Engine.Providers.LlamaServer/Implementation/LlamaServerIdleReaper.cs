namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;
using ProcessKey = LlamaServerProcessSupervisor.ProcessKey;
using RunningProcess = LlamaServerProcessSupervisor.RunningProcess;

/// <summary>
///     Owns the loaded-model population for <see cref="LlamaServerProcessSupervisor" />: cap admission with LRU
///     eviction, the background idle reaper, exited-process pruning, and the detach plus tree-kill teardown every
///     removal path funnels through.
/// </summary>
/// <remarks>
///     Holds the supervisor's LIVE process table, never a snapshot, so a reaper pass and a spawn admission always
///     decide over the same <c>RunningProcess</c> set. INVARIANT: a live process holding an active inference lease is
///     never torn down here, not by the idle reaper past the TTL and not as a cap-admission victim, because
///     <c>LastUsedUtc</c> is stamped when a request starts and when its lease is released, not per token, so a long
///     generation looks idle while a request is mid-flight. Gate scope, victim ranking and the detach invariants: wiki 03, "Eviction &amp; reaper".
/// </remarks>
internal sealed class LlamaServerIdleReaper : IDisposable
{
    /// <summary>How long after readiness a never-leased chat process is spared by memory eviction, so its spawning request can lease it first.</summary>
    internal static readonly TimeSpan FirstLeaseGracePeriod = TimeSpan.FromSeconds(30);

    // Guards the loaded-cap admission decision + port-set mutation so the cap can never be exceeded by a race.
    private readonly SemaphoreSlim _admissionGate = new(initialCount: 1, maxCount: 1);

    // The supervisor's LIVE process table, by reference. Forking a copy here would let the reaper evict a process the
    // supervisor still hands out (and miss one it registered), so this is never snapshotted into a field.
    private readonly ConcurrentDictionary<ProcessKey, RunningProcess> _processes;
    private readonly LlamaServerPortAllocator _ports;
    private readonly ILlamaLayerPlacementReport _layerPlacementReport;
    private readonly LlamaServerSupervisorOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly IRuntimeResidencyChangeNotifier _residencyNotifier;

    internal LlamaServerIdleReaper(ConcurrentDictionary<ProcessKey, RunningProcess> processes,
        LlamaServerPortAllocator ports,
        ILlamaLayerPlacementReport layerPlacementReport,
        LlamaServerSupervisorOptions options,
        TimeProvider timeProvider,
        ILogger logger,
        IRuntimeResidencyChangeNotifier? residencyNotifier = null)
    {
        _residencyNotifier = residencyNotifier ?? NullRuntimeResidencyChangeNotifier.Instance;
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _ports = ports ?? throw new ArgumentNullException(nameof(ports));
        _layerPlacementReport = layerPlacementReport ?? throw new ArgumentNullException(nameof(layerPlacementReport));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void Dispose()
    {
        _admissionGate.Dispose();
    }

    /// <summary>
    ///     Reserves a slot under the loaded-cap (evicting an idle LRU process to make room when possible) and allocates
    ///     a free localhost port. The admission gate serializes the cap decision so it can never be raced past.
    /// </summary>
    /// <remarks>
    ///     The cap is measured by the <em>reserved-port</em> count, not the registered-process count, so it already
    ///     includes in-flight spawns — see wiki 03, "Eviction &amp; reaper". A <see cref="ModelResidencyIntent.Background" />
    ///     spawn never evicts a live chat process; with no other victim it is refused like any spawn at the cap.
    /// </remarks>
    internal async Task<int> AdmitAndAllocatePortAsync(ModelResidencyIntent intent, CancellationToken ct)
    {
        // Processes detached from the table under the gate, tree-killed after it is released (see KillDetachedProcesses).
        var detached = new List<RunningProcess>();
        await _admissionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Drop any process that has already exited so its slot/port is reclaimed before the cap check.
            PruneExitedProcesses(detached);

            if (_ports.ReservedCount >= _options.MaxLoadedProcesses && !TryEvictIdleLeastRecentlyUsed(intent, detached))
            {
                throw CapReached();
            }

            return _ports.Allocate();
        }
        finally
        {
            _admissionGate.Release();

            // The gate is free BEFORE any child is killed, because a multi-GB tree-kill under it serializes every unrelated model's port allocation and release.
            // This spawn still waits for its own victim to die before it launches, so the VRAM the victim held is genuinely released first.
            KillDetachedProcesses(detached);
        }
    }

    /// <summary>
    ///     Background reaper: evicts processes idle beyond <see cref="LlamaServerSupervisorOptions.IdleTimeToLive" />, or
    ///     beyond the shorter transient lifetime for a transient process.
    /// </summary>
    internal async Task ReapIdleLoopAsync(CancellationToken ct)
    {
        // Re-check at a fraction of the shorter TTL so eviction latency stays bounded without busy-spinning.
        var interval = TimeSpan.FromTicks(Math.Max(_options.EffectiveTransientIdleTimeToLive.Ticks / 4, TimeSpan.FromSeconds(1).Ticks));
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(interval, _timeProvider, ct).ConfigureAwait(false);
                await ReapIdleOnceAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    internal async Task ReapIdleOnceAsync()
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var (key, running) in _processes.ToArray())
        {
            // A live profiling process is never idle-evicted mid-benchmark; an EXITED one is still reaped below so a dead handle never leaks. IsProfilingOwned
            // covers the registration-to-Pin() window, where the pin alone does not yet protect it.
            if ((running.IsProfilingPinned || running.IsProfilingOwned) && !running.Handle.HasExited)
            {
                continue;
            }

            // A live process with in-flight inference (an active lease) is never reaped, even past the TTL: LastUsedUtc is stamped at request start and lease release, not
            // per token, so a generation that legitimately outruns the idle window (a raised invocation timeout on a slow CPU machine) looks idle while mid-flight.
            if (running.ActiveLeases > 0 && !running.Handle.HasExited)
            {
                continue;
            }

            var ttl = IdleTimeToLiveFor(running);
            if (running.Handle.HasExited || now - running.LastUsedUtc >= ttl)
            {
                if (!running.Handle.HasExited)
                {
                    _logger.LogInformation("Evicting idle {Residency} llama-server for model {ModelName} role {Role} (idle {IdleSeconds:F0}s past TTL {TtlSeconds:F0}s).",
                        running.IsTransient ? "transient" : "interactive", key.ModelName, key.Role, (now - running.LastUsedUtc).TotalSeconds, ttl.TotalSeconds);
                }

                await RemoveProcessAsync(key, running).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    ///     Evicts the least-recently-used process that is currently idle; the caller holds the admission gate.
    /// </summary>
    /// <remarks>
    ///     The victim is detached here — its slot and port are free the moment this returns <see langword="true" /> —
    ///     and appended to <paramref name="detached" /> for the caller to tree-kill once the gate is released. The
    ///     ranking it applies is in wiki 03, "Eviction &amp; reaper".
    /// </remarks>
    private bool TryEvictIdleLeastRecentlyUsed(ModelResidencyIntent intent, List<RunningProcess> detached)
    {
        var now = _timeProvider.GetUtcNow();
        ProcessKey? victimKey = null;
        RunningProcess? victim = null;
        var victimRank = int.MaxValue;
        foreach (var (key, running) in _processes)
        {
            // A live profiling process is reserved for its benchmark and is never a cap-admission victim; an EXITED one is a dead handle and stays eligible so its
            // slot and port are reclaimed. IsProfilingOwned covers the registration-to-Pin() window, where a pooled-role profiling process would be LRU-eligible.
            if ((running.IsProfilingPinned || running.IsProfilingOwned) && !running.Handle.HasExited)
            {
                continue;
            }

            // In-flight inference disqualifies a live process for the same reason the idle reaper skips it: past-TTL only means "no new request started", not
            // "not mid-generation". This is a best-effort heuristic read; the atomic claim is TryBeginEvict on the chosen victim below.
            if (running.ActiveLeases > 0 && !running.Handle.HasExited)
            {
                continue;
            }

            // Work no user waits on never unloads a chat model, idle past its TTL or transient alike; an exited handle is still reclaimed.
            if (intent == ModelResidencyIntent.Background && key.Role == ModelRole.Chat && !running.Handle.HasExited)
            {
                continue;
            }

            // Victim preference, best first: rank 0 is exited or idle past its TTL in any role, rank 1 an in-window but unleased POOLED role or TRANSIENT
            // process. An in-window interactive CHAT process is never a victim. Why the pooled roles yield and the chat role does not: wiki 03, "Eviction and reaper".
            var isIdlePastTtl = running.Handle.HasExited || now - running.LastUsedUtc >= IdleTimeToLiveFor(running);
            var yieldsInWindow = key.Role is ModelRole.Embedding or ModelRole.Reranker || running.IsTransient;
            if (!isIdlePastTtl && !yieldsInWindow)
            {
                continue; // An in-window interactive chat process is never a victim.
            }

            var rank = isIdlePastTtl ? 0 : 1;

            // A better rank always wins; within the same rank, least-recently-used wins.
            if (victim is not null && (rank != victimRank ? rank > victimRank : running.LastUsedUtc >= victim.LastUsedUtc))
            {
                continue;
            }

            victimKey = key;
            victim = running;
            victimRank = rank;
        }

        if (victimKey is null || victim is null || !TryClaimAndDetach(victimKey.Value, victim, detached))
        {
            return false;
        }

        _logger.LogWarning("Loaded-model cap ({Cap}) reached; evicting {Idleness} llama-server for model {ModelName} role {Role} to admit a new one.",
            _options.MaxLoadedProcesses, victimRank == 0 ? "idle" : "in-window pooled or transient", victimKey.Value.ModelName, victimKey.Value.Role);
        return true;
    }

    /// <summary>
    ///     Unloads the least-recently-used live chat process no request is using, to free memory for a cold chat load the capacity
    ///     gate refused on budget, and returns its key; or, when none is idle, a key skipped only for its active lease.
    /// </summary>
    /// <remarks>
    ///     Unlike <see cref="TryEvictIdleLeastRecentlyUsed" />, an in-window interactive chat process IS a victim here (operator decision: a chat
    ///     load may unload any chat model without a request in flight). Never the requesting key, a pooled role (the node's embedder and
    ///     reranker), a <paramref name="protectedModels" /> name, or a profiling process. The victim is tree-killed before this returns, outside
    ///     the gate; the caller's bounded re-probe covers the driver releasing its VRAM. Wiki 03, "Eviction &amp; reaper".
    /// </remarks>
    internal async Task<(ProcessKey? Evicted, ProcessKey? Busy)> TryEvictIdleChatForMemoryAsync(ProcessKey requesting,
        IReadOnlyCollection<string> protectedModels,
        CancellationToken ct)
    {
        var detached = new List<RunningProcess>();
        await _admissionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = _timeProvider.GetUtcNow();
            ProcessKey? victimKey = null;
            ProcessKey? busyKey = null;
            RunningProcess? victim = null;
            foreach (var (key, running) in _processes)
            {
                // An exited process holds no memory to free. A process awaiting its first lease was just loaded for a request that has not leased it
                // yet, so it is spared, but only for a grace period: a warm-only load (the model picker) never leases at all.
                if (key.Role != ModelRole.Chat
                    || key.Equals(requesting)
                    || protectedModels.Contains(key.ModelName, StringComparer.OrdinalIgnoreCase)
                    || running.Handle.HasExited
                    || running.IsProfilingPinned
                    || running.IsProfilingOwned
                    || running.AwaitingFirstLease && now - running.ReadyUtc < FirstLeaseGracePeriod)
                {
                    continue;
                }

                if (running.ActiveLeases > 0)
                {
                    busyKey ??= key;
                    continue;
                }

                if (victim is null || running.LastUsedUtc < victim.LastUsedUtc)
                {
                    victimKey = key;
                    victim = running;
                }
            }

            if (victimKey is null || victim is null)
            {
                return (null, busyKey);
            }

            // A lease taken between the scan and the claim makes the victim busy, not ineligible.
            if (!TryClaimAndDetach(victimKey.Value, victim, detached))
            {
                return (null, victimKey);
            }

            _logger.LogWarning("Evicting idle chat llama-server for model {ModelName} to free memory for a load of model {RequestedModelName}.",
                victimKey.Value.ModelName, requesting.ModelName);
            return (victimKey, null);
        }
        finally
        {
            _admissionGate.Release();
            KillDetachedProcesses(detached);
        }
    }

    /// <summary>Atomically claims <paramref name="victim" /> and detaches it into <paramref name="detached" /> for a kill outside the gate.</summary>
    /// <remarks>The caller holds the admission gate.</remarks>
    private bool TryClaimAndDetach(ProcessKey key, RunningProcess victim, List<RunningProcess> detached)
    {
        // Atomically latch the chosen victim before tearing it down: if a request took a lease between the heuristic scan and here, TryBeginEvict fails and no
        // victim is admitted this round, so the caller surfaces its error. An EXITED victim holds no real lease and is torn down regardless.
        if (!victim.Handle.HasExited && !victim.TryBeginEvict(forProfiling: false, out _))
        {
            return false;
        }

        // Free the slot and port under the gate so the new admission proceeds immediately; the kill follows outside it. A lost removal race (a concurrent eject or
        // reap already detached this victim) frees nothing of OUR doing, so report no eviction rather than act on someone else's teardown.
        if (DetachProcess(key, victim) is not { } evicted)
        {
            return false;
        }

        detached.Add(evicted);
        return true;
    }

    private TimeSpan IdleTimeToLiveFor(RunningProcess running) =>
        running.IsTransient ? _options.EffectiveTransientIdleTimeToLive : _options.IdleTimeToLive;

    private void PruneExitedProcesses(List<RunningProcess> detached)
    {
        foreach (var (key, running) in _processes)
        {
            if (running.Handle.HasExited && DetachProcess(key, running) is { } exited)
            {
                detached.Add(exited);
            }
        }
    }

    internal async Task RemoveProcessAsync(ProcessKey key, RunningProcess running)
    {
        // Teardown must complete even during shutdown, so it is not bound to a caller cancellation token.
        RunningProcess? detached;
        await _admissionGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            detached = DetachProcess(key, running);
        }
        finally
        {
            _admissionGate.Release();
        }

        // Killed OUTSIDE the gate so a multi-GB tree-kill does not serialize unrelated admissions, but still awaited by this caller: callers, notably the profiling
        // path's ambient-VRAM baseline, rely on the child being gone when this returns.
        if (detached is not null)
        {
            KillDetachedProcess(detached);
        }
    }

    /// <summary>
    ///     Removes a process from the table, retires its measured layer placement and releases its port reservation —
    ///     everything that makes the slot available to the next admission — WITHOUT touching the child.
    /// </summary>
    /// <remarks>
    ///     The caller holds the admission gate. Returns the process when this call won the removal race, the caller
    ///     then owing it a <see cref="KillDetachedProcess" />, or <see langword="null" /> when a concurrent path already
    ///     removed it. This is the ONLY place a process leaves <see cref="_processes" />, and INVARIANT: the port
    ///     reservation is dropped here, BEFORE the child is killed, so the reservation set that bounds the cap never
    ///     counts a process on its way out. Why both, and why that is still safe: wiki 03, "Eviction &amp; reaper".
    /// </remarks>
    internal RunningProcess? DetachProcess(ProcessKey key, RunningProcess running)
    {
        if (!_processes.TryRemove(new KeyValuePair<ProcessKey, RunningProcess>(key, running)))
        {
            return null; // Already removed by a concurrent path.
        }

        // Every deliberate removal detaches BEFORE it kills and registration follows readiness, so a child already dead here died outside the node. Logged
        // here, after the winning TryRemove, so whichever path notices the exit (prune, same-key respawn, reaper, eject) leaves exactly one trace.
        if (running.Handle.HasExited)
        {
            _logger.LogWarning(
                "llama-server for model {ModelName} role {Role} (pid {ProcessId}) exited outside the supervisor's control with exit code {ExitCode}; it was not evicted by the node and is respawned on the next request.",
                key.ModelName, key.Role, running.Handle.ProcessId, running.Handle.ExitCode);
        }

        _layerPlacementReport.Remove(key.Role, key.ModelName);
        _ports.Release(running.Port);
        _residencyNotifier.NotifyChanged();
        return running;
    }

    /// <summary>Tree-kills + disposes a detached process. Never called while the admission gate is held.</summary>
    internal static void KillDetachedProcess(RunningProcess running)
    {
        try
        {
            running.Handle.TreeKill();
        }
        finally
        {
            running.Handle.Dispose();
        }
    }

    /// <summary>
    ///     Tree-kills every process detached during an admission decision.
    /// </summary>
    /// <remarks>
    ///     A teardown failure is logged, never rethrown: the admission it trails has already succeeded, or failed with
    ///     its own cap error, and turning a kill failure into the caller's exception would both mask that error and
    ///     skip the remaining victims.
    /// </remarks>
    private void KillDetachedProcesses(List<RunningProcess> detached)
    {
        foreach (var running in detached)
        {
            try
            {
                KillDetachedProcess(running);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Tearing down an evicted llama-server (pid {ProcessId}) failed; its slot and port were already released.",
                    running.Handle.ProcessId);
            }
        }
    }

    /// <summary>
    ///     Releases a reserved port for a spawn that never registered (launch/readiness failure), taking the admission
    ///     gate so the reserved-port set (which backs the cap count) is mutated under the same lock as allocation.
    /// </summary>
    internal async Task ReleaseReservedPortAsync(int port)
    {
        await _admissionGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            _ports.Release(port);
        }
        finally
        {
            _admissionGate.Release();
        }
    }

    private static LlamaRuntimeException CapReached()
    {
        return LlamaServerProcessSupervisor.NonRetryable("The maximum number of local models are already loaded. Unload a model or raise the limit, then try again.");
    }
}

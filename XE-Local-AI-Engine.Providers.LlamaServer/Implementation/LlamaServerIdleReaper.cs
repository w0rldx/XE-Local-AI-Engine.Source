namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
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
///     <c>LastUsedUtc</c> is stamped per ensure or reuse, not per token, so a long generation looks idle while a
///     request is mid-flight. Gate scope, victim ranking and the detach invariants: wiki 03, "Eviction &amp; reaper".
/// </remarks>
internal sealed class LlamaServerIdleReaper : IDisposable
{
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

    internal LlamaServerIdleReaper(ConcurrentDictionary<ProcessKey, RunningProcess> processes,
        LlamaServerPortAllocator ports,
        ILlamaLayerPlacementReport layerPlacementReport,
        LlamaServerSupervisorOptions options,
        TimeProvider timeProvider,
        ILogger logger)
    {
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
    ///     includes in-flight spawns — see wiki 03, "Eviction &amp; reaper".
    /// </remarks>
    internal async Task<int> AdmitAndAllocatePortAsync(CancellationToken ct)
    {
        // Processes detached from the table under the gate, tree-killed after it is released (see KillDetachedProcesses).
        var detached = new List<RunningProcess>();
        await _admissionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Drop any process that has already exited so its slot/port is reclaimed before the cap check.
            PruneExitedProcesses(detached);

            if (_ports.ReservedCount >= _options.MaxLoadedProcesses && !TryEvictIdleLeastRecentlyUsed(detached))
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

    /// <summary>Background reaper: evicts processes idle beyond <see cref="LlamaServerSupervisorOptions.IdleTimeToLive" />.</summary>
    internal async Task ReapIdleLoopAsync(CancellationToken ct)
    {
        // Re-check at a fraction of the TTL so eviction latency stays bounded without busy-spinning.
        var interval = TimeSpan.FromTicks(Math.Max(_options.IdleTimeToLive.Ticks / 4, TimeSpan.FromSeconds(1).Ticks));
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

            // A live process with in-flight inference (an active lease) is never reaped, even past the TTL: LastUsedUtc is stamped per ensure or reuse, not per
            // token, so a generation that legitimately outruns the idle window (a raised invocation timeout on a slow CPU box) looks idle while mid-flight.
            if (running.ActiveLeases > 0 && !running.Handle.HasExited)
            {
                continue;
            }

            if (running.Handle.HasExited || now - running.LastUsedUtc >= _options.IdleTimeToLive)
            {
                if (!running.Handle.HasExited)
                {
                    _logger.LogInformation("Evicting idle llama-server for model {ModelName} role {Role} (idle {IdleSeconds:F0}s past TTL {TtlSeconds:F0}s).",
                        key.ModelName, key.Role, (now - running.LastUsedUtc).TotalSeconds, _options.IdleTimeToLive.TotalSeconds);
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
    private bool TryEvictIdleLeastRecentlyUsed(List<RunningProcess> detached)
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

            // Victim preference, best first: rank 0 is exited or idle past the TTL in any role, rank 1 an in-window but unleased POOLED role. An in-window CHAT
            // process is never a victim. Why the pooled roles yield and the chat role does not: wiki 03, "Eviction and reaper".
            var isIdlePastTtl = running.Handle.HasExited || now - running.LastUsedUtc >= _options.IdleTimeToLive;
            var isPooledRole = key.Role is ModelRole.Embedding or ModelRole.Reranker;
            if (!isIdlePastTtl && !isPooledRole)
            {
                continue; // An in-window chat process is never a victim.
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

        if (victimKey is null || victim is null)
        {
            return false;
        }

        // Atomically latch the chosen victim before tearing it down: if a request took a lease between the heuristic scan and here, TryBeginEvict fails and no
        // victim is admitted this round, so the caller surfaces the cap error. An EXITED victim holds no real lease and is torn down regardless.
        if (!victim.Handle.HasExited && !victim.TryBeginEvict(forProfiling: false, out _))
        {
            return false;
        }

        // Free the slot and port under the gate so the new admission proceeds immediately; the kill follows outside it. A lost removal race (a concurrent eject or
        // reap already detached this victim) frees no slot of OUR doing, so report no admission rather than let the cap be overrun on someone else's teardown.
        if (DetachProcess(victimKey.Value, victim) is not { } evicted)
        {
            return false;
        }

        _logger.LogWarning("Loaded-model cap ({Cap}) reached; evicting {Idleness} llama-server for model {ModelName} role {Role} to admit a new one.",
            _options.MaxLoadedProcesses, victimRank == 0 ? "idle" : "in-window pooled", victimKey.Value.ModelName, victimKey.Value.Role);

        detached.Add(evicted);
        return true;
    }

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

        _layerPlacementReport.Remove(key.Role, key.ModelName);
        _ports.Release(running.Port);
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

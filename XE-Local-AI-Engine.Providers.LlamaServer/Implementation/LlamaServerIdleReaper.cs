namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;
using XE_Local_AI_Engine.Providers.ProcessSupervision;
using XE_Local_AI_Engine.Providers.ProcessSupervision.Contracts;
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

    private static readonly TimeSpan HeldSurvivorWarningInterval = TimeSpan.FromMinutes(1);

    // Guards the loaded-cap admission decision + port-set mutation so the cap can never be exceeded by a race.
    private readonly SemaphoreSlim _admissionGate = new(initialCount: 1, maxCount: 1);

    // The supervisor's LIVE process table, by reference. Forking a copy here would let the reaper evict a process the
    // supervisor still hands out (and miss one it registered), so this is never snapshotted into a field.
    private readonly ConcurrentDictionary<ProcessKey, RunningProcess> _processes;
    private readonly LlamaServerPortAllocator _ports;

    // Vetoed children whose exit was never confirmed. Each keeps its port reserved, so it still counts against the cap, until it is seen to exit. Under the gate.
    private readonly List<UnconfirmedKill> _unconfirmedKills = [];
    private int _unconfirmedKillCount;
    private readonly ILlamaLayerPlacementReport _layerPlacementReport;
    private readonly LlamaServerSupervisorOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly IRuntimeResidencyChangeNotifier _residencyNotifier;
    private readonly ILlamaServerLoadTelemetry _loadTelemetry;
    private readonly ILlamaServerPooledLaunchAdmission? _pooledLaunchAdmission;

    internal LlamaServerIdleReaper(ConcurrentDictionary<ProcessKey, RunningProcess> processes,
        LlamaServerPortAllocator ports,
        ILlamaLayerPlacementReport layerPlacementReport,
        LlamaServerSupervisorOptions options,
        TimeProvider timeProvider,
        ILogger logger,
        IRuntimeResidencyChangeNotifier? residencyNotifier = null,
        ILlamaServerLoadTelemetry? loadTelemetry = null,
        ILlamaServerPooledLaunchAdmission? pooledLaunchAdmission = null)
    {
        _residencyNotifier = residencyNotifier ?? NullRuntimeResidencyChangeNotifier.Instance;
        _loadTelemetry = loadTelemetry ?? new NullLlamaServerLoadTelemetry();
        _pooledLaunchAdmission = pooledLaunchAdmission;
        ArgumentNullException.ThrowIfNull(processes);
        _processes = processes;
        ArgumentNullException.ThrowIfNull(ports);
        _ports = ports;
        ArgumentNullException.ThrowIfNull(layerPlacementReport);
        _layerPlacementReport = layerPlacementReport;
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public void Dispose()
    {
        _admissionGate.Dispose();
    }

    /// <summary>
    ///     Reserves a slot under the role's loaded-cap (evicting an idle LRU process of the same class to make room when
    ///     possible) and allocates a free localhost port. The admission gate serializes the cap decision so it can never be raced past.
    /// </summary>
    /// <remarks>
    ///     Chat processes count against <see cref="LlamaServerSupervisorOptions.MaxLoadedProcesses" />, companions (embedding,
    ///     reranker, decision) against <see cref="LlamaServerSupervisorOptions.MaxLoadedCompanionProcesses" />, each by its
    ///     <em>reserved-port</em> count, so in-flight spawns are included — see wiki 03, "Eviction &amp; reaper". A
    ///     <see cref="ModelResidencyIntent.Background" /> spawn never evicts a live chat process. The cap is read under the gate and as many
    ///     victims are evicted as it needs; out of victims, the admission is refused and the ones already detached are still torn down.
    /// </remarks>
    internal async Task<int> AdmitAndAllocatePortAsync(ModelRole role, ModelResidencyIntent intent, CancellationToken ct)
    {
        var companion = IsCompanion(role);
        // Processes detached from the table under the gate, tree-killed after it is released (see KillDetachedProcesses).
        var detached = new List<RunningProcess>();
        var exitedSurvivors = new List<IProcessTreeHandle>();
        await _admissionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Drop any process that has already exited so its slot/port is reclaimed before the cap check.
            PruneExitedProcesses(detached);
            ReclaimExitedUnconfirmedKills(exitedSurvivors);

            var cap = companion ? _options.MaxLoadedCompanionProcesses : _options.MaxLoadedProcesses;
            while (_ports.ReservedCountOf(companion) >= cap)
            {
                // A cancelled admission stops evicting; the finally below still tears down every victim it already detached.
                ct.ThrowIfCancellationRequested();
                if (!TryEvictIdleLeastRecentlyUsed(intent, companion, cap, detached))
                {
                    // A held survivor occupies a slot of its own class only, so only one of the admitting class explains the refusal.
                    throw CapReached(_unconfirmedKills.Exists(kill => IsCompanion(kill.Key.Role) == companion));
                }
            }

            return _ports.Allocate(companion);
        }
        finally
        {
            _admissionGate.Release();

            // The gate is free BEFORE any child is killed, because a multi-GB tree-kill under it serializes every unrelated model's port allocation and release.
            // This spawn still waits for its own victim to die before it launches, so the VRAM the victim held is genuinely released first.
            KillDetachedProcesses(detached);
            DisposeExited(exitedSurvivors);
        }
    }

    /// <summary>
    ///     Background reaper: evicts processes idle beyond <see cref="LlamaServerSupervisorOptions.IdleTimeToLive" />, or
    ///     beyond the shorter transient lifetime for a transient process, then unloads chat processes above a lowered cap.
    /// </summary>
    /// <remarks>Paced by the shorter, transient TTL, so a transient process is reaped on time too.</remarks>
    internal Task ReapIdleLoopAsync(CancellationToken ct) =>
        IdleReapLoop.RunAsync(() => _options.EffectiveTransientIdleTimeToLive, _timeProvider, () => ReapIdlePassAsync(ct), _logger, ct);

    /// <summary>One reaper pass outside the background loop, with nothing to cancel its keep-warm read.</summary>
    internal Task ReapIdleOnceAsync() =>
        ReapIdlePassAsync(CancellationToken.None);

    /// <summary>One reaper pass; <paramref name="ct" /> (shutdown) cancels only the keep-warm read, never a teardown or a gate wait.</summary>
    private async Task ReapIdlePassAsync(CancellationToken ct)
    {
        await RetryUnconfirmedKillsAsync().ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();
        foreach (var (key, running) in _processes.ToArray())
        {
            // A teardown failure must not fault the reaper loop (it would never run again) or skip the remaining processes.
            try
            {
                // An exited child is cleaned up unconditionally, profiling or leased alike, so a dead handle never leaks.
                if (running.Handle.HasExited)
                {
                    await RemoveProcessAsync(key, running).ConfigureAwait(false);
                    continue;
                }

                // Never a live profiling process (IsProfilingOwned covers the registration-to-Pin() window) or one with an active lease: LastUsedUtc is stamped at request
                // start and lease release, not per token, so a generation outrunning the idle window (a raised invocation timeout on a slow CPU machine) looks idle mid-flight.
                if (running.IsProfilingPinned || running.IsProfilingOwned || running.ActiveLeases > 0 || now - running.LastUsedUtc < IdleTimeToLiveFor(running))
                {
                    continue;
                }

                _logger.LogInformation("Evicting idle {Residency} llama-server for model {ModelName} role {Role} (idle {IdleSeconds:F0}s past TTL {TtlSeconds:F0}s).",
                    running.IsTransient ? "transient" : "interactive", key.ModelName, key.Role, (now - running.LastUsedUtc).TotalSeconds, IdleTimeToLiveFor(running).TotalSeconds);
                await ReapExpiredAsync(key, running).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Tearing down an idle llama-server for model {ModelName} role {Role} (pid {ProcessId}) failed; its slot and port were already released.",
                    key.ModelName, key.Role, running.Handle.ProcessId);
            }
        }

        await ReconcileLoweredChatCapAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Tears down a process the pass found idle past its TTL, unless a request used it since; the kill runs outside the gate.</summary>
    /// <remarks>
    ///     The scan ran outside the gate, so a request may have leased the process, or leased and released it, since. The claim comes first:
    ///     while it is held no lease can start, and a lease that ended before it stamped the idle clock first, so the expiry read after it is
    ///     final. A fresh process gets its claim back. The unload is recorded only for a detach this pass won. Accepted ceiling: a lease
    ///     requested in the instant the claim is held is refused as evicting, as during an admission's claim.
    /// </remarks>
    private async Task ReapExpiredAsync(ProcessKey key, RunningProcess running)
    {
        var detached = new List<RunningProcess>();
        await _admissionGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (!running.TryBeginEvict(forProfiling: false, out var claim))
            {
                _logger.LogDebug("Kept llama-server for model {ModelName} role {Role}: a request leased it after the idle scan, or another teardown owns it.", key.ModelName, key.Role);
                return;
            }

            if (_timeProvider.GetUtcNow() - running.LastUsedUtc < IdleTimeToLiveFor(running))
            {
                running.ReleaseEvictionClaim(claim);
                _logger.LogDebug("Kept llama-server for model {ModelName} role {Role}: a request used it after the idle scan.", key.ModelName, key.Role);
                return;
            }

            if (DetachProcess(key, running) is not { } evicted)
            {
                return; // A concurrent teardown already removed it.
            }

            detached.Add(evicted);

            // A child that exited since the scan died outside the node; DetachProcess already logged it as such.
            if (!running.Handle.HasExited)
            {
                RecordUnload(key, LlamaServerUnloadReason.IdleTimeout);
            }
        }
        finally
        {
            _admissionGate.Release();
            KillDetachedProcesses(detached);
        }
    }

    /// <summary>
    ///     Unloads chat processes, best victim first, while more chat slots are reserved than <see cref="LlamaServerSupervisorOptions.MaxLoadedProcesses" />
    ///     allows; only a lowered cap leaves the node above it, since admission never admits past the cap.
    /// </summary>
    /// <remarks>
    ///     Victims rank by <see cref="AboveCapRank" />, LRU first within a rank; never a companion or a process <see cref="IsUnloadableChat" />
    ///     rules out. A leased one goes on the first pass after its lease ends; in-flight spawns and held survivors count but never go.
    ///     Keep-warm names are read outside the gate: unreadable, nothing goes; one changed during the pass applies on the next.
    /// </remarks>
    private async Task ReconcileLoweredChatCapAsync(CancellationToken ct)
    {
        // Reserved ports, not registered processes: an in-flight spawn or a held survivor occupies a slot a registered chat must yield.
        await _admissionGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_ports.ReservedCountOf(companion: false) <= _options.MaxLoadedProcesses)
            {
                return;
            }
        }
        finally
        {
            _admissionGate.Release();
        }

        IReadOnlyCollection<string> protectedModels;
        try
        {
            protectedModels = _pooledLaunchAdmission is null
                ? []
                : await _pooledLaunchAdmission.GetProtectedChatModelsAsync(ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Only a shutdown cancellation escapes, ending the loop quietly; anything else costs this pass its reconciliation.
            _logger.LogWarning(exception, "Reading the keep-warm model failed; chat processes above the loaded-model cap are unloaded on a later pass.");
            return;
        }

        var detached = new List<RunningProcess>();
        var exitedSurvivors = new List<IProcessTreeHandle>();
        await _admissionGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            // A held survivor that exited since the early check frees its slot here, so a live chat is not unloaded in its place.
            PruneExitedProcesses(detached);
            ReclaimExitedUnconfirmedKills(exitedSurvivors);
            while (_ports.ReservedCountOf(companion: false) > _options.MaxLoadedProcesses)
            {
                if (!TryEvictChatAboveCap(protectedModels, detached))
                {
                    break;
                }
            }
        }
        finally
        {
            _admissionGate.Release();
            KillDetachedProcesses(detached);
            DisposeExited(exitedSurvivors);
        }
    }

    /// <summary>Detaches the best chat victim above a lowered cap into <paramref name="detached" />; the caller holds the admission gate.</summary>
    private bool TryEvictChatAboveCap(IReadOnlyCollection<string> protectedModels, List<RunningProcess> detached)
    {
        var now = _timeProvider.GetUtcNow();
        ProcessKey? victimKey = null;
        RunningProcess? victim = null;
        var victimRank = int.MaxValue;
        foreach (var (key, running) in _processes)
        {
            if (!IsUnloadableChat(key, running, protectedModels, now) || running.ActiveLeases > 0)
            {
                continue;
            }

            var rank = AboveCapRank(running, now);
            if (victim is not null && !IsBetterVictim(rank, running, victimRank, victim))
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

        _logger.LogWarning("Loaded-model cap ({Cap}) is below the loaded chat models; unloading the llama-server for model {ModelName}.",
            _options.MaxLoadedProcesses, victimKey.Value.ModelName);
        RecordUnload(victimKey.Value, LlamaServerUnloadReason.LoadedModelCap);
        return true;
    }

    /// <summary>Victim rank above a lowered cap, best first: 0 idle past its TTL, 1 an in-window transient process, 2 an in-window interactive chat.</summary>
    private int AboveCapRank(RunningProcess running, DateTimeOffset now)
    {
        if (now - running.LastUsedUtc >= IdleTimeToLiveFor(running))
        {
            return 0;
        }

        return running.IsTransient ? 1 : 2;
    }

    /// <summary>
    ///     Whether a live chat process may be unloaded on demand, for a memory shortfall or a lowered cap: never a <paramref name="protectedModels" />
    ///     name (the keep-warm model), a profiling process, or one still within <see cref="FirstLeaseGracePeriod" /> of its first lease.
    /// </summary>
    /// <remarks>
    ///     A process awaiting its first lease was just loaded for a request that has not leased it yet, so it is spared, but only for the grace
    ///     period: a warm-only load (the model picker) never leases at all. An active lease is each caller's own check.
    /// </remarks>
    private static bool IsUnloadableChat(ProcessKey key, RunningProcess running, IReadOnlyCollection<string> protectedModels, DateTimeOffset now) =>
        key.Role == ModelRole.Chat
        && !running.Handle.HasExited
        && !running.IsProfilingPinned
        && !running.IsProfilingOwned
        && !(running.AwaitingFirstLease && now - running.ReadyUtc < FirstLeaseGracePeriod)
        && !protectedModels.Contains(key.ModelName, StringComparer.OrdinalIgnoreCase);

    /// <summary>A better (lower) rank always wins; within the same rank, the least recently used wins.</summary>
    private static bool IsBetterVictim(int rank, RunningProcess candidate, int victimRank, RunningProcess victim) =>
        rank != victimRank ? rank < victimRank : candidate.LastUsedUtc < victim.LastUsedUtc;

    /// <summary>
    ///     Evicts the least-recently-used process that is currently idle; the caller holds the admission gate.
    /// </summary>
    /// <remarks>
    ///     The victim is detached here — its slot and port are free the moment this returns <see langword="true" /> —
    ///     and appended to <paramref name="detached" /> for the caller to tree-kill once the gate is released. The
    ///     ranking it applies is in wiki 03, "Eviction &amp; reaper".
    /// </remarks>
    private bool TryEvictIdleLeastRecentlyUsed(ModelResidencyIntent intent, bool companion, int cap, List<RunningProcess> detached)
    {
        var now = _timeProvider.GetUtcNow();
        ProcessKey? victimKey = null;
        RunningProcess? victim = null;
        var victimRank = int.MaxValue;
        foreach (var (key, running) in _processes)
        {
            // Each class has its own slots, so only a process of the admitting class frees one: companions never evict a chat model or the reverse.
            if (IsCompanion(key.Role) != companion)
            {
                continue;
            }

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

            // Victim preference, best first: rank 0 is exited or idle past its TTL, rank 1 an in-window but unleased companion or TRANSIENT process.
            // An in-window interactive CHAT process is never a victim. Ranking and the per-class caps: wiki 03, "Eviction and reaper".
            var isIdlePastTtl = running.Handle.HasExited || now - running.LastUsedUtc >= IdleTimeToLiveFor(running);
            var yieldsInWindow = companion || running.IsTransient;
            if (!isIdlePastTtl && !yieldsInWindow)
            {
                continue; // An in-window interactive chat process is never a victim.
            }

            var rank = isIdlePastTtl ? 0 : 1;

            if (victim is not null && !IsBetterVictim(rank, running, victimRank, victim))
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
            cap, victimRank == 0 ? "idle" : "in-window pooled or transient", victimKey.Value.ModelName, victimKey.Value.Role);
        if (!victim.Handle.HasExited)
        {
            RecordUnload(victimKey.Value, LlamaServerUnloadReason.LoadedModelCap);
        }

        return true;
    }

    /// <summary>
    ///     Unloads the least-recently-used live chat process no request is using, to free memory for a cold chat load the capacity
    ///     gate refused on budget, and returns its key; or, when none is idle, a key skipped only for its active lease.
    /// </summary>
    /// <remarks>
    ///     Unlike <see cref="TryEvictIdleLeastRecentlyUsed" />, an in-window interactive chat process IS a victim here (by design: a chat
    ///     load may unload any chat model without a request in flight). Never the requesting key, a pooled role (the node's embedder and
    ///     reranker), a <paramref name="protectedModels" /> name, or a profiling process. The victim is tree-killed before this returns, outside
    ///     the gate; the caller's bounded re-probe covers the driver releasing its VRAM. Wiki 03, "Eviction &amp; reaper".
    /// </remarks>
    internal async Task<ChatEvictionOutcome> TryEvictIdleChatForMemoryAsync(ProcessKey requesting,
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
                if (key.Equals(requesting) || !IsUnloadableChat(key, running, protectedModels, now))
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
                return new ChatEvictionOutcome(null, busyKey);
            }

            // A lease taken between the scan and the claim makes the victim busy, not ineligible.
            if (!TryClaimAndDetach(victimKey.Value, victim, detached))
            {
                return new ChatEvictionOutcome(null, victimKey);
            }

            _logger.LogWarning("Evicting idle chat llama-server for model {ModelName} to free memory for a load of model {RequestedModelName}.",
                victimKey.Value.ModelName, requesting.ModelName);
            RecordUnload(victimKey.Value, LlamaServerUnloadReason.MemoryForAnotherModel);
            return new ChatEvictionOutcome(victimKey, null);
        }
        finally
        {
            _admissionGate.Release();
            KillDetachedProcesses(detached);
        }
    }

    /// <summary>The evicted key, or else a key skipped only for its active lease; both null when nothing qualified.</summary>
    internal readonly record struct ChatEvictionOutcome(ProcessKey? Evicted, ProcessKey? Busy);

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

    /// <summary>Reports a deliberate teardown to the load telemetry; report-only, so a failing sink never changes an eviction.</summary>
    internal void RecordUnload(ProcessKey key, LlamaServerUnloadReason reason)
    {
        try
        {
            _loadTelemetry.RecordUnload(key.ModelName, key.Role, reason);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "llama-server unload telemetry observer failed.");
        }
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

    /// <summary>Whether a vetoed child not yet seen to exit is held; read without the gate by the runtime-mutation check.</summary>
    internal bool HasUnconfirmedKills => Volatile.Read(ref _unconfirmedKillCount) > 0;

    /// <summary>
    ///     Takes over a vetoed child whose kill was not confirmed: its port stays reserved, so the cap still counts it, until a
    ///     reap or an admission sees it exit.
    /// </summary>
    internal async Task HoldUntilExitedAsync(ProcessKey key, IProcessTreeHandle handle, int port)
    {
        await _admissionGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            _unconfirmedKills.Add(new UnconfirmedKill(key, handle, port)
            {
                LastWarnedUtc = _timeProvider.GetUtcNow()
            });
            Volatile.Write(ref _unconfirmedKillCount, _unconfirmedKills.Count);
        }
        finally
        {
            _admissionGate.Release();
        }
    }

    /// <summary>Kills every held survivor again, outside the gate, then reclaims the ones that exited. Never throws.</summary>
    internal async Task RetryUnconfirmedKillsAsync()
    {
        UnconfirmedKill[] pending;
        await _admissionGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            pending = [.. _unconfirmedKills];
        }
        finally
        {
            _admissionGate.Release();
        }

        if (pending.Length == 0)
        {
            return;
        }

        // A retry kills only where the handle can: on Windows the job is already closed, so only the wait for the exit remains.
        foreach (var handle in pending.Select(static kill => kill.Handle))
        {
            try
            {
                handle.TreeKill();
            }
            catch (Exception exception)
            {
                _logger.LogDebug(exception, "Killing a vetoed llama-server (pid {ProcessId}) failed again.", handle.ProcessId);
            }
        }

        var exited = new List<IProcessTreeHandle>();
        await _admissionGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            ReclaimExitedUnconfirmedKills(exited);
            WarnHeldSurvivors();
        }
        finally
        {
            _admissionGate.Release();
        }

        DisposeExited(exited);
    }

    /// <summary>Releases the port of every held survivor that has exited and hands its handle back for disposal outside the gate; the caller holds the gate.</summary>
    private void ReclaimExitedUnconfirmedKills(List<IProcessTreeHandle> exited)
    {
        for (var i = _unconfirmedKills.Count - 1; i >= 0; i--)
        {
            var kill = _unconfirmedKills[i];
            if (kill.Handle.HasExited)
            {
                _ports.Release(kill.Port);
                _unconfirmedKills.RemoveAt(i);
                exited.Add(kill.Handle);
            }
        }

        Volatile.Write(ref _unconfirmedKillCount, _unconfirmedKills.Count);
    }

    /// <summary>Names each held survivor at Warning, at most once per <see cref="HeldSurvivorWarningInterval" />; the caller holds the gate.</summary>
    private void WarnHeldSurvivors()
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var kill in _unconfirmedKills.Where(kill => now - kill.LastWarnedUtc >= HeldSurvivorWarningInterval))
        {
            kill.LastWarnedUtc = now;
            _logger.LogWarning("A vetoed llama-server for model {ModelName} role {Role} (pid {ProcessId}) has still not exited after its kill; it occupies a loaded-model slot until it does.",
                kill.Key.ModelName, kill.Key.Role, kill.Handle.ProcessId);
        }
    }

    private void DisposeExited(List<IProcessTreeHandle> exited)
    {
        foreach (var handle in exited)
        {
            try
            {
                handle.Dispose();
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Disposing an exited vetoed llama-server (pid {ProcessId}) failed.", handle.ProcessId);
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

    private static bool IsCompanion(ModelRole role) =>
        role is ModelRole.Embedding or ModelRole.Reranker or ModelRole.Decision;

    /// <summary>A vetoed child the supervisor still owns, with the port it keeps reserved and when it was last named in a warning.</summary>
    private sealed class UnconfirmedKill
    {
        public UnconfirmedKill(ProcessKey key, IProcessTreeHandle handle, int port)
        {
            Key = key;
            Handle = handle;
            Port = port;
        }

        public ProcessKey Key { get; }

        public IProcessTreeHandle Handle { get; }

        public int Port { get; }

        public DateTimeOffset LastWarnedUtc { get; set; }
    }

    private static LlamaRuntimeException CapReached(bool survivorHeld)
    {
        return LlamaServerProcessSupervisor.NonRetryable(survivorHeld
            ? "The maximum number of local models are already loaded, and one slot is held by an ejected model that is still shutting down. Unload a model, raise the limit, or wait, then try again."
            : "The maximum number of local models are already loaded. Unload a model or raise the limit, then try again.");
    }
}

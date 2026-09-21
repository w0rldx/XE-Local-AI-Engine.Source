namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;

/// <summary>
///     Lease and eviction half of <see cref="LlamaServerProcessSupervisor" />: the inference-lease counter that keeps a
///     process alive while requests are in flight, the graceful drain that waits for it to reach zero, and the evict /
///     eject paths that tear a process down once it has.
/// </summary>
public sealed partial class LlamaServerProcessSupervisor
{
    /// <inheritdoc />
    public async Task EvictAsync(string modelName, ModelRole role, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        _runtimeMutationGate.BeginOperation();
        try
        {
            await EvictCoreAsync(modelName, role).ConfigureAwait(false);
        }
        finally
        {
            _runtimeMutationGate.EndOperation();
        }
    }

    private async Task EvictCoreAsync(string modelName, ModelRole role)
    {
        var key = new ProcessKey(modelName, role);
        if (_processes.TryGetValue(key, out var running))
        {
            await _reaper.RemoveProcessAsync(key, running).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task EvictAllRolesAsync(string modelName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        _runtimeMutationGate.BeginOperation();
        try
        {
            await EvictAllRolesCoreAsync(modelName).ConfigureAwait(false);
        }
        finally
        {
            _runtimeMutationGate.EndOperation();
        }
    }

    private async Task EvictAllRolesCoreAsync(string modelName)
    {
        foreach (var role in Enum.GetValues<ModelRole>())
        {
            await EvictCoreAsync(modelName, role).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Profiling's pre-spawn eviction: lease-aware and two-phase unlike the operator <see cref="EvictCoreAsync" />
    ///     force path, because a measurement is never worth killing a live generation for.
    /// </summary>
    /// <returns>
    ///     The refusing role, what it was serving and why, or <see langword="null" /> when every role was evicted.
    /// </returns>
    /// <remarks>
    ///     Every live role is CLAIMED first through <see cref="RunningProcess.TryBeginEvict(bool, out long)" />, the
    ///     same atomic check-and-mark cap admission uses, and only a complete set is torn down — a half-evicted model
    ///     for a run that never happens is worse than no eviction. A role serving in-flight inference refuses its
    ///     claim, and claims already taken are released through <see cref="RunningProcess.ReleaseEvictionClaim" />: an
    ///     abandoned claim refuses every future lease, an ownership-blind clear erases an operator eject's own mark.
    /// </remarks>
    private async Task<(ModelRole Role, int ActiveLeases, LlamaServerProfilingRefusalReason Reason)?> TryEvictAllRolesForProfilingAsync(string modelName)
    {
        var claimed = new List<(ProcessKey Key, RunningProcess Process, long Claim)>();
        var exited = new List<(ProcessKey Key, RunningProcess Process, long Claim)>();
        foreach (var role in Enum.GetValues<ModelRole>())
        {
            var key = new ProcessKey(modelName, role);
            if (!_processes.TryGetValue(key, out var running))
            {
                continue;
            }

            // An exited process holds no real lease: it is reaped with the rest so its slot and port are reclaimed,
            // and it is never claimed, so a refusal leaves an operator eject's own mark on it untouched.
            if (running.Handle.HasExited)
            {
                exited.Add((key, running, Claim: 0));
                continue;
            }

            if (!running.TryBeginEvict(forProfiling: true, out var claim, out var alreadyEvicting))
            {
                foreach (var (_, claimedProcess, claimedToken) in claimed)
                {
                    claimedProcess.ReleaseEvictionClaim(claimedToken);
                }

                // Sampled HERE, in the refusal branch: a lost compare-exchange means another teardown owns the process
                // and there is no lease count to report, so the reason carries the meaning instead of a made-up number.
                var activeLeases = alreadyEvicting ? 0 : running.ActiveLeases;
                var reason = alreadyEvicting
                    ? LlamaServerProfilingRefusalReason.EvictionAlreadyInProgress
                    : LlamaServerProfilingRefusalReason.InUse;
                _logger.LogInformation("Profiling for model {ModelName} was skipped: role {Role} could not be claimed ({Reason}, {ActiveLeases} in-flight request(s)).",
                    modelName, role, reason, activeLeases);
                return (role, activeLeases, reason);
            }

            claimed.Add((key, running, claim));
        }

        foreach (var (key, running, _) in exited.Concat(claimed))
        {
            await _reaper.RemoveProcessAsync(key, running).ConfigureAwait(false);
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<LlamaServerEjectOutcome> EjectAsync(string modelName, ModelRole role, bool force, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        _runtimeMutationGate.BeginOperation();
        try
        {
            return await EjectCoreAsync(modelName, role, force, ct).ConfigureAwait(false);
        }
        finally
        {
            _runtimeMutationGate.EndOperation();
        }
    }

    private async Task<LlamaServerEjectOutcome> EjectCoreAsync(string modelName, ModelRole role, bool force, CancellationToken ct)
    {
        var key = new ProcessKey(modelName, role);
        if (!_processes.TryGetValue(key, out var target) || target.Handle.HasExited)
        {
            // Nothing live to eject. Reap a lingering dead entry so its slot/port frees, then report an idempotent no-op.
            if (target is not null)
            {
                await _reaper.RemoveProcessAsync(key, target).ConfigureAwait(false);
            }

            return LlamaServerEjectOutcome.NotRunning;
        }

        // Mark evicting: new inference leases are refused, so the active-lease count can only fall while we drain, and the process stays registered and reusable until
        // we tear it down or give up. The claim is kept so every release below clears only THIS eject's mark, never one a later teardown took over.
        var evictionClaim = target.MarkEvicting();
        _logger.LogInformation("Operator eject requested for model {ModelName} role {Role} (force: {Force}); draining {ActiveLeases} in-flight request(s).",
            key.ModelName, key.Role, force, target.ActiveLeases);

        bool drained;
        try
        {
            drained = await DrainLeasesAsync(target, ct).ConfigureAwait(false);
        }
        catch
        {
            // The drain itself was aborted (the eject request was cancelled mid-drain): no teardown happened, so the
            // evicting mark must not outlive this call — left set, the process would refuse every future lease forever.
            target.ReleaseEvictionClaim(evictionClaim);
            throw;
        }

        if (drained)
        {
            await _reaper.RemoveProcessAsync(key, target).ConfigureAwait(false);
            _logger.LogInformation("Operator eject completed for model {ModelName} role {Role}: drained and torn down.", key.ModelName, key.Role);
            return LlamaServerEjectOutcome.Ejected;
        }

        if (force)
        {
            // Force: tear down despite in-flight work. Mark ejected FIRST so the interrupted request's leaseholder can
            // classify the resulting connection failure as an operator eject rather than a generic provider drop.
            target.MarkEjected();
            await _reaper.RemoveProcessAsync(key, target).ConfigureAwait(false);
            _logger.LogWarning("Operator eject FORCED for model {ModelName} role {Role}: {ActiveLeases} in-flight request(s) interrupted.",
                key.ModelName, key.Role, target.ActiveLeases);
            return LlamaServerEjectOutcome.ForcedWhileBusy;
        }

        // Busy and not forced: never kill silently. Leave the process running and usable, and report that the eject
        // could not complete safely so the caller can decide (retry / force).
        target.ReleaseEvictionClaim(evictionClaim);
        _logger.LogInformation("Operator eject for model {ModelName} role {Role} did not complete: still busy after the drain window; left running.", key.ModelName, key.Role);
        return LlamaServerEjectOutcome.TimedOutStillBusy;
    }

    /// <inheritdoc />
    public LlamaServerLeaseAcquisition TryAcquireInferenceLease(string modelName, ModelRole role)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        var key = new ProcessKey(modelName, role);

        if (!_processes.TryGetValue(key, out var running) || running.Handle.HasExited)
        {
            return LlamaServerLeaseAcquisition.NotRunning;
        }

        // A profiling-owned process is invisible to inference: callers ensure first then look the lease up by key, so without this a chat whose process was replaced would lease the
        // transient one and die with it. Its OWN refusal, not NotRunning, which licenses a leaseless call against the already-resolved endpoint — commonly the port the measurement just took.
        if (running.IsProfilingOwned)
        {
            return LlamaServerLeaseAcquisition.ProfilingOwned;
        }

        // A draining eject refuses new leases, and the refusal REASON is surfaced so the caller fails the request as operator-ejected rather than run it leaseless under the
        // drain — untracked by it, killed mid-flight by the teardown, and then self-heal-respawning the model just ejected.
        var eviction = running.EvictionOwner;
        if (eviction != 0)
        {
            return Refusal(eviction);
        }

        // Acquire, then RE-CHECK evicting/exited: an eject that flipped the flag between the guard above and here must
        // not gain a lease that would extend its drain — release and refuse, classifying the refusal at this instant.
        running.AcquireLease();
        eviction = running.EvictionOwner;
        if (eviction != 0 || running.Handle.HasExited)
        {
            running.ReleaseLease();
            return eviction != 0 ? Refusal(eviction) : LlamaServerLeaseAcquisition.NotRunning;
        }

#pragma warning disable CA2000 // Ownership of the lease transfers to the caller inside the returned acquisition; the interface contract obliges the caller to dispose it.
        return LlamaServerLeaseAcquisition.Granted(new InferenceLease(running));
#pragma warning restore CA2000
    }

    /// <summary>
    ///     Classifies a refusal against a process whose teardown has begun, taking the owning claim the caller already
    ///     read so the classification cannot straddle two reads.
    /// </summary>
    /// <remarks>
    ///     A profiling pre-spawn eviction is a transient benchmark spawn, not an operator eject: reported as the
    ///     latter, a chat fails terminally with "the model is being ejected by the operator" for something that clears
    ///     itself in seconds, and embedding and rerank misreport the same way. Reported as its own refusal, the caller
    ///     lands in the bounded re-ensure arm instead.
    /// </remarks>
    private static LlamaServerLeaseAcquisition Refusal(long evictionOwner) =>
        evictionOwner < 0 ? LlamaServerLeaseAcquisition.ProfilingOwned : LlamaServerLeaseAcquisition.Evicting;

    /// <summary>
    ///     Waits, bounded by <see cref="LlamaServerSupervisorOptions.EjectDrainTimeout" />, for a process's active
    ///     inference leases to drain to zero.
    /// </summary>
    /// <remarks>
    ///     <see langword="true" /> when drained within the window — an idle process returns immediately —
    ///     <see langword="false" /> when the window elapsed with work still in flight. The drain window is real-time
    ///     bounded rather than on the injected clock, being an actual wall-clock wait, and a caller cancellation
    ///     propagates as an <see cref="OperationCanceledException" />.
    /// </remarks>
    private async Task<bool> DrainLeasesAsync(RunningProcess running, CancellationToken ct)
    {
        if (running.ActiveLeases == 0)
        {
            return true;
        }

        using var drainCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        drainCts.CancelAfter(_options.EjectDrainTimeout);
        try
        {
            while (running.ActiveLeases > 0)
            {
                await Task.Delay(LeaseDrainPollInterval, drainCts.Token).ConfigureAwait(false);
            }

            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The drain window elapsed with work still in flight (not a caller cancellation).
            return running.ActiveLeases == 0;
        }
    }
}

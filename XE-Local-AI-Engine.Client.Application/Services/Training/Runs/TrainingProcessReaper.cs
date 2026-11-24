namespace XE_Local_AI_Engine.Client.Services.Training.Runs;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;
using XE_Local_AI_Engine.Providers.Training.Contracts;

/// <summary>Finds and kills what a recorded launch receipt still describes, for every startup sweep over a Python job.</summary>
/// <remarks>
///     Every recorded field must match before anything is signaled; only a changed start time, or a changed run token once the group the
///     pid names is empty, proves a recycled pid. That is the <c>SandboxOrphanReaper</c> model; <c>StaleProcessReaper</c>'s
///     kill-by-executable-root model would reap any Python this node runs, since the job's executable is the shared venv interpreter.
/// </remarks>
internal static class TrainingProcessReaper
{
    /// <summary>True only when every recorded field still describes a live process.</summary>
    public static bool Matches(TrainingLaunchReceiptV1 receipt, TrainingProcessFacts? facts)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        return facts is not null
               // The live group may be the pid itself: a receipt read before setsid(2) ran recorded the host's group.
               && (facts.Pgid == receipt.Pgid || facts.Pgid == receipt.Pid)
               && facts.StartTicks == receipt.StartTicks
               // No recorded path means the spawn timed out before the exec; start ticks survive exec, so identity still holds.
               && (string.IsNullOrEmpty(receipt.ExecutablePath) || string.Equals(facts.ExecutablePath, receipt.ExecutablePath, StringComparison.Ordinal))
               && string.Equals(facts.RunToken, receipt.RunToken, StringComparison.Ordinal)
               && receipt.RunToken.Length > 0;
    }

    /// <summary>False for a receipt too incomplete to probe (no pid, start time or run token): its reap can never prove the process gone.</summary>
    public static bool CanIdentify(TrainingLaunchReceiptV1 receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        return receipt.Pid > 1 && receipt.StartTicks > 0 && !string.IsNullOrEmpty(receipt.RunToken);
    }

    public static TrainingLaunchReceiptV1 ToPersisted(TrainingLaunchReceipt receipt) =>
        new()
        {
            Pid = receipt.Pid,
            Pgid = receipt.Pgid,
            ExecutablePath = receipt.ExecutablePath,
            StartTicks = receipt.StartTicks,
            RunToken = receipt.RunToken,
            BootId = receipt.BootId
        };

    /// <summary>Inspects one receipt, clears it only once it is safe to, and reports whether the process is proven gone.</summary>
    /// <remarks>
    ///     A receipt whose inspect or kill THREW is left in place and retried on the next startup: dropping it there would strand a
    ///     live process with nothing left to identify it by. Failures are per-receipt: one unreadable <c>/proc</c> entry must not
    ///     abandon the rest. A row is still cleared on an unreadable entry, as before; the outcome is for callers that must not. A kill
    ///     deferred by an operational failure (EPERM, EMFILE), or a boot-bound receipt while this boot's id is unreadable, keeps the receipt
    ///     too. A receipt of a previous boot is gone without a probe.
    /// </remarks>
    public static async Task<TrainingReapOutcome> ReapOneAsync(ITrainingProcessInspector inspector,
        Guid ownerId,
        ReadOnlyMemory<byte>? receiptJson,
        Func<CancellationToken, Task> clearReceipt,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            TrainingProcessPresence presence;
            if (Read(receiptJson) is not { } receipt)
            {
                // A row's unparsable receipt can never be matched, so it is cleared; failing to identify a process is still no proof it exited.
                logger.LogWarning("The recorded trainer receipt for run {RunId} could not be read; no process could be identified or signalled.", ownerId);
                presence = receiptJson is null ? TrainingProcessPresence.Gone : TrainingProcessPresence.Unknown;
            }
            else if (receipt.BootId is not null && inspector.CurrentBootId is null)
            {
                // Bound to a boot this host cannot read: neither same nor previous boot is proven, so it is kept like a deferred kill.
                logger.LogWarning(
                    "The trainer receipt for run {RunId} records a boot, but this boot's id could not be read; nothing was signalled and its receipt was kept so the next startup retries it.",
                    ownerId);
                return TrainingReapOutcome.NotVerified;
            }
            else if (receipt.BootId is not null && !string.Equals(receipt.BootId, inspector.CurrentBootId, StringComparison.Ordinal))
            {
                logger.LogInformation("The trainer receipt for pid {Pid} was written in a previous boot, which no process outlives; nothing was signalled.", receipt.Pid);
                presence = TrainingProcessPresence.Gone;
            }
            else if (!CanIdentify(receipt))
            {
                // Probing pid 0 reads as gone, and a zero start time "differs" from any live one: neither is evidence of an exit.
                logger.LogWarning("The recorded trainer receipt for run {RunId} is incomplete (pid {Pid}); no process could be identified or signalled.",
                    ownerId, receipt.Pid);
                presence = TrainingProcessPresence.Unknown;
            }
            else if (inspector.Inspect(receipt.Pid) is not { } facts)
            {
                presence = inspector.ProbePresence(receipt.Pid);
                presence = presence == TrainingProcessPresence.Gone && GroupOutlivesLeader(inspector, receipt, logger) ? TrainingProcessPresence.Unknown : presence;
                logger.LogInformation("A recorded trainer receipt for pid {Pid} has no readable live process ({Presence}); nothing was signalled.",
                    receipt.Pid, presence);
            }
            else if (!Matches(receipt, facts))
            {
                // The pid exists; only a field read and found different proves it is not ours. An unreadable one leaves it unknown.
                presence = Contradicts(receipt, facts) ? TrainingProcessPresence.Gone : TrainingProcessPresence.Unknown;
                // Only a changed start time proves the old group empty; a token-only mismatch leaves the pid's group to be probed.
                presence = presence == TrainingProcessPresence.Gone && facts.StartTicks == receipt.StartTicks && GroupOutlivesLeader(inspector, receipt, logger)
                    ? TrainingProcessPresence.Unknown
                    : presence;
                logger.LogInformation("A recorded trainer receipt for pid {Pid} does not match a live process ({Presence}); nothing was signalled.",
                    receipt.Pid, presence);
            }
            else if (TrainingProcessGroupGuard.MaySignalGroup(receipt.Pid, facts.Pgid, inspector.HostProcessGroupId))
            {
                logger.LogWarning("Reaping a trainer process group {Pgid} left behind by a previous host process.", facts.Pgid);
                presence = await inspector.KillProcessGroupAsync(facts.Pgid, receipt.StartTicks, receipt.RunToken, cancellationToken);
            }
            else
            {
                // A trainer that never led its own group shares one with a host or launcher; only its pid is safe.
                logger.LogWarning("Reaping trainer pid {Pid} left behind by a previous host process; its group {Pgid} is not its own, so only the pid is signalled.",
                    receipt.Pid, receipt.Pgid);
                presence = await inspector.KillProcessAsync(receipt.Pid, receipt.StartTicks, receipt.RunToken, cancellationToken);
            }

            if (presence == TrainingProcessPresence.Deferred)
            {
                logger.LogWarning("The trainer recorded for run {RunId} could not be signalled for an operational reason; its receipt was kept so the next startup retries it.",
                    ownerId);
                return TrainingReapOutcome.NotVerified;
            }

            await clearReceipt(cancellationToken);
            return presence == TrainingProcessPresence.Gone ? TrainingReapOutcome.Verified : TrainingReapOutcome.NotVerified;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception,
                "Reaping the trainer recorded for run {RunId} failed; its receipt was kept so the next startup retries it.",
                ownerId);
            return TrainingReapOutcome.NotVerified;
        }
    }

    /// <summary>True when the group the trainer leads still has members although its leader reads gone: a pid is not reallocated while it names a group.</summary>
    /// <remarks>The group is the pid: a trainer runs <c>setsid</c>, whatever group the receipt recorded before that.</remarks>
    private static bool GroupOutlivesLeader(ITrainingProcessInspector inspector, TrainingLaunchReceiptV1 receipt, ILogger logger)
    {
        if (receipt.Pid <= 1 || receipt.Pid == inspector.HostProcessGroupId || !inspector.ProcessGroupHasMembers(receipt.Pid))
        {
            return false;
        }

        logger.LogWarning("The recorded trainer pid {Pid} reads gone, but the process group it leads still has members; nothing was signalled, and it is not reported gone.", receipt.Pid);
        return true;
    }

    /// <summary>True when the start time, or a token that was read, disagrees with the receipt: a recycled pid. An unreadable token contradicts nothing.</summary>
    /// <remarks>
    ///     A process keeps its pid and start time for life, but its executable path can change under it (a replaced interpreter reads
    ///     with a <c> (deleted)</c> suffix) and it may move to another group, so a mismatch there alone leaves it unknown, never gone.
    /// </remarks>
    private static bool Contradicts(TrainingLaunchReceiptV1 receipt, TrainingProcessFacts facts) =>
        facts.StartTicks != receipt.StartTicks
        || (facts.RunToken is not null && !string.Equals(facts.RunToken, receipt.RunToken, StringComparison.Ordinal));

    private static TrainingLaunchReceiptV1? Read(ReadOnlyMemory<byte>? payload)
    {
        if (payload is not { } bytes || bytes.IsEmpty)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<TrainingLaunchReceiptV1>(bytes.Span, TrainingJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

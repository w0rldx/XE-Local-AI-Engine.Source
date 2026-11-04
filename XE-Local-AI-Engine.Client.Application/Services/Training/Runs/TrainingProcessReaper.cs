namespace XE_Local_AI_Engine.Client.Services.Training.Runs;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;
using XE_Local_AI_Engine.Providers.Training.Contracts;

/// <summary>Finds and kills what a recorded launch receipt still describes, for every startup sweep over a Python job.</summary>
/// <remarks>
///     Every recorded field is validated before anything is signaled, because any mismatch means a recycled pid. That is the
///     <c>SandboxOrphanReaper</c> model; <c>StaleProcessReaper</c>'s kill-by-executable-root model would reap any Python this
///     node runs, since the job's executable is the shared venv interpreter.
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

    public static TrainingLaunchReceiptV1 ToPersisted(TrainingLaunchReceipt receipt) =>
        new()
        {
            Pid = receipt.Pid,
            Pgid = receipt.Pgid,
            ExecutablePath = receipt.ExecutablePath,
            StartTicks = receipt.StartTicks,
            RunToken = receipt.RunToken
        };

    /// <summary>Inspects one receipt and clears it only once it is safe to.</summary>
    /// <remarks>
    ///     A receipt whose inspect or kill THREW is left in place and retried on the next startup: dropping it there would strand a
    ///     live process with nothing left to identify it by. Failures are per-receipt: one unreadable <c>/proc</c> entry must not
    ///     abandon the rest.
    /// </remarks>
    public static async Task ReapOneAsync(ITrainingProcessInspector inspector,
        Guid ownerId,
        ReadOnlyMemory<byte>? receiptJson,
        Func<CancellationToken, Task> clearReceipt,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            if (Read(receiptJson) is not { } receipt)
            {
                // A receipt this host cannot parse can never be matched, so it can only ever block its own removal.
                logger.LogWarning("The recorded trainer receipt for run {RunId} could not be read; it was cleared.", ownerId);
            }
            else if (inspector.Inspect(receipt.Pid) is not { } facts || !Matches(receipt, facts))
            {
                logger.LogInformation("A recorded trainer receipt for pid {Pid} no longer matches a live process; nothing was signalled.",
                    receipt.Pid);
            }
            else if (TrainingProcessGroupGuard.MaySignalGroup(receipt.Pid, facts.Pgid, inspector.HostProcessGroupId))
            {
                logger.LogWarning("Reaping a trainer process group {Pgid} left behind by a previous host process.", facts.Pgid);
                await inspector.KillProcessGroupAsync(facts.Pgid, receipt.StartTicks, cancellationToken);
            }
            else
            {
                // A trainer that never led its own group shares one with a host or launcher; only its pid is safe.
                logger.LogWarning("Reaping trainer pid {Pid} left behind by a previous host process; its group {Pgid} is not its own, so only the pid is signalled.",
                    receipt.Pid, receipt.Pgid);
                await inspector.KillProcessAsync(receipt.Pid, receipt.StartTicks, cancellationToken);
            }

            await clearReceipt(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception,
                "Reaping the trainer recorded for run {RunId} failed; its receipt was kept so the next startup retries it.",
                ownerId);
        }
    }

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

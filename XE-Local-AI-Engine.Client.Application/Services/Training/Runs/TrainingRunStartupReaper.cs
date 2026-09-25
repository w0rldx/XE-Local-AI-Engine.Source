namespace XE_Local_AI_Engine.Client.Services.Training.Runs;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;
using XE_Local_AI_Engine.Providers.Training.Contracts;

/// <summary>
///     Startup sweep for what a previous process left behind: a trainer that outlived its host, and the decrypted
///     scratch its run never got to delete.
/// </summary>
/// <remarks>
///     Every recorded field is validated before anything is signalled — pid alive, process-group id, executable realpath,
///     <c>/proc</c> start time, and the run token in the child's own environment — because any mismatch means a recycled pid.
///     That is the <c>SandboxOrphanReaper</c> model; <c>StaleLlamaServerReaper</c>'s kill-by-executable-root model would reap
///     any Python this node runs, since the trainer's executable is the shared venv interpreter. The scratch sweep is age-gated
///     like <c>GgufAcquisitionArtifactStartupReaper</c>: a <c>work/</c> directory past the stale window holds decrypted data.
/// </remarks>
public sealed class TrainingRunStartupReaper : IHostedService
{
    internal static readonly TimeSpan StaleWorkAge = TimeSpan.FromHours(6);

    private readonly ITrainingProcessInspector _inspector;
    private readonly ILogger<TrainingRunStartupReaper> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly TrainingRunWorkspace _workspace;

    public TrainingRunStartupReaper(IServiceScopeFactory scopeFactory,
        ITrainingProcessInspector inspector,
        TrainingRunWorkspace workspace,
        TimeProvider timeProvider,
        ILogger<TrainingRunStartupReaper> logger)
    {
        ArgumentNullException.ThrowIfNull(inspector);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(workspace);
        _inspector = inspector;
        _logger = logger;
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _workspace = workspace;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ReapAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A sweep failure must never block node startup.
            _logger.LogError(exception, "The training run startup sweep failed.");
        }

        SweepStaleWork();
    }

    public Task StopAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    /// <summary>
    ///     True only when every recorded field still describes a live process. Public for the test that walks each
    ///     field's mismatch individually — the guarantee is per-field, so it has to be provable per-field.
    /// </summary>
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

    private async Task ReapAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ITrainingRunStore>();

        // Every receipt, unpaged: a live trainer whose run sits behind a page of newer runs is exactly the one that
        // must not be missed. Recovery no longer touches the column, so the read order against it does not matter.
        foreach (var entry in await store.ListLaunchReceiptsAsync(cancellationToken))
        {
            await ReapOneAsync(store, entry, cancellationToken);
        }

        var failed = await store.RecoverOnStartupAsync(cancellationToken);
        if (failed.Count > 0)
        {
            _logger.LogWarning("Marked {RunCount} training runs interrupted by the previous shutdown as failed; interrupted runs are never resumed.",
                failed.Count);
        }
    }

    /// <summary>Inspects one receipt and clears it only once it is safe to.</summary>
    /// <remarks>
    ///     A receipt whose inspect or kill THREW is left in place and retried on the next startup — dropping it there
    ///     would strand a live trainer with nothing left to identify it by. Failures are per-receipt: one unreadable
    ///     <c>/proc</c> entry must not abandon the rest.
    /// </remarks>
    private async Task ReapOneAsync(ITrainingRunStore store, TrainingRunLaunchReceipt entry, CancellationToken cancellationToken)
    {
        try
        {
            if (Read(entry.LaunchReceiptJson) is not { } receipt)
            {
                // A receipt this host cannot parse can never be matched, so it can only ever block its own removal.
                _logger.LogWarning("The recorded trainer receipt for run {RunId} could not be read; it was cleared.", entry.RunId);
            }
            else if (_inspector.Inspect(receipt.Pid) is not { } facts || !Matches(receipt, facts))
            {
                _logger.LogInformation("A recorded trainer receipt for pid {Pid} no longer matches a live process; nothing was signalled.",
                    receipt.Pid);
            }
            else if (TrainingProcessGroupGuard.MaySignalGroup(receipt.Pid, facts.Pgid, _inspector.HostProcessGroupId))
            {
                _logger.LogWarning("Reaping a trainer process group {Pgid} left behind by a previous host process.", facts.Pgid);
                await _inspector.KillProcessGroupAsync(facts.Pgid, receipt.StartTicks, cancellationToken);
            }
            else
            {
                // A trainer that never led its own group shares one with a host or launcher; only its pid is safe.
                _logger.LogWarning("Reaping trainer pid {Pid} left behind by a previous host process; its group {Pgid} is not its own, so only the pid is signalled.",
                    receipt.Pid, receipt.Pgid);
                await _inspector.KillProcessAsync(receipt.Pid, receipt.StartTicks, cancellationToken);
            }

            await store.SetLaunchReceiptAsync(entry.RunId, launchReceiptJson: null, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception,
                "Reaping the trainer recorded for run {RunId} failed; its receipt was kept so the next startup retries it.",
                entry.RunId);
        }
    }

    private void SweepStaleWork()
    {
        var root = _workspace.RunsRoot;
        if (!Directory.Exists(root))
        {
            return;
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        foreach (var runDirectory in Directory.EnumerateDirectories(root))
        {
            var work = Path.Combine(runDirectory, "work");
            try
            {
                var info = new DirectoryInfo(work);
                if (info.Exists && now - info.LastWriteTimeUtc >= StaleWorkAge)
                {
                    info.Delete(recursive: true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(exception, "A stale training work directory could not be removed; the next sweep retries it.");
            }
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

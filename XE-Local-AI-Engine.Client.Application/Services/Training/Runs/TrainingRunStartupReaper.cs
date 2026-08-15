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
///     <para>
///         The receipt is validated on EVERY recorded field before anything is signalled — pid alive, process-group id,
///         executable realpath, <c>/proc</c> start time, and the run token in the child's own environment. Any single
///         mismatch means the pid was recycled and this is somebody else's process, so nothing is signalled at all.
///         This is the <c>SandboxOrphanReaper</c> model. <c>StaleLlamaServerReaper</c>'s model — kill anything whose
///         executable lives under a known root — is the one to avoid here: the trainer's executable is the shared venv
///         interpreter, so a path match would reap any Python this node happens to be running.
///     </para>
///     <para>
///         <strong>This class is the only thing that clears a receipt on startup, and it clears one only after the
///         process behind it is provably gone</strong> — killed, or ruled out as a recycled pid. Everything else
///         (<see cref="ITrainingRunStore.RecoverOnStartupAsync" />) leaves the column alone, because terminalizing a
///         run row says nothing about the trainer: a sweep that dropped receipts on the way past would turn a live
///         orphan into an unkillable one still holding its GPU allocation. That is also why the receipts are read
///         unpaged and why a receipt whose inspect or kill threw survives to the next startup.
///     </para>
///     <para>
///         The scratch sweep is age-gated like <c>GgufAcquisitionArtifactStartupReaper</c>: a <c>work/</c> directory
///         older than the stale window belonged to a run that is long gone, and holds decrypted training data.
///     </para>
/// </remarks>
public sealed class TrainingRunStartupReaper(
    IServiceScopeFactory scopeFactory,
    ITrainingProcessInspector inspector,
    TrainingRunWorkspace workspace,
    TimeProvider timeProvider,
    ILogger<TrainingRunStartupReaper> logger) : IHostedService
{
    internal static readonly TimeSpan StaleWorkAge = TimeSpan.FromHours(6);

    private readonly ITrainingProcessInspector _inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
    private readonly ILogger<TrainingRunStartupReaper> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly TrainingRunWorkspace _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ReapAsync(cancellationToken).ConfigureAwait(false);
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
               && facts.Pgid == receipt.Pgid
               && facts.StartTicks == receipt.StartTicks
               && string.Equals(facts.ExecutablePath, receipt.ExecutablePath, StringComparison.Ordinal)
               && string.Equals(facts.RunToken, receipt.RunToken, StringComparison.Ordinal)
               && receipt.RunToken.Length > 0;
    }

    private async Task ReapAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ITrainingRunStore>();

        // Every receipt, unpaged: a live trainer whose run sits behind a page of newer runs is exactly the one that
        // must not be missed. Recovery no longer touches the column, so the read order against it does not matter.
        foreach (var entry in await store.ListLaunchReceiptsAsync(cancellationToken).ConfigureAwait(false))
        {
            await ReapOneAsync(store, entry, cancellationToken).ConfigureAwait(false);
        }

        _ = await store.RecoverOnStartupAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Inspects one receipt and clears it only once it is safe to. A receipt whose inspect or kill THREW is left in
    ///     place and retried on the next startup — dropping it there would strand a live trainer with nothing left to
    ///     identify it by. Failures are per-receipt: one unreadable <c>/proc</c> entry must not abandon the rest.
    /// </summary>
    private async Task ReapOneAsync(ITrainingRunStore store, TrainingRunLaunchReceipt entry, CancellationToken cancellationToken)
    {
        try
        {
            if (Read(entry.LaunchReceiptJson) is not { } receipt)
            {
                // A receipt this host cannot parse can never be matched, so it can only ever block its own removal.
                _logger.LogWarning("The recorded trainer receipt for run {RunId} could not be read; it was cleared.", entry.RunId);
            }
            else if (!Matches(receipt, _inspector.Inspect(receipt.Pid)))
            {
                _logger.LogInformation("A recorded trainer receipt for pid {Pid} no longer matches a live process; nothing was signalled.",
                    receipt.Pid);
            }
            else
            {
                _logger.LogWarning("Reaping a trainer process group {Pgid} left behind by a previous host process.", receipt.Pgid);
                await _inspector.KillProcessGroupAsync(receipt.Pgid, cancellationToken).ConfigureAwait(false);
            }

            await store.SetLaunchReceiptAsync(entry.RunId, launchReceiptJson: null, cancellationToken).ConfigureAwait(false);
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

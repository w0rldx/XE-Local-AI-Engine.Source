namespace XE_Local_AI_Engine.Client.Services.ModelLab;

using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Training.Runs;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Training.Contracts;

/// <summary>What a previous process left behind: a warm worker, a live job, the rows it was writing, and its plaintext work directory.</summary>
/// <remarks>
///     The work directories go unconditionally, not by age: a job never resumes, and nothing runs before this does. A receipt whose
///     kill failed stays for the next startup, and its row is still failed: the row says the experiment is over, not that the process is.
///     A warm worker's receipt file stays the same way, and Model Lab refuses starts while it does.
/// </remarks>
public sealed class ModelLabStartupReaper : IHostedService
{
    private readonly INodeDataDirectory _dataDirectory;
    private readonly ModelLabExperimentService _experiments;
    private readonly ITrainingProcessInspector _inspector;
    private readonly ILogger<ModelLabStartupReaper> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    public ModelLabStartupReaper(IServiceScopeFactory scopeFactory,
        ITrainingProcessInspector inspector,
        INodeDataDirectory dataDirectory,
        ModelLabExperimentService experiments,
        ILogger<ModelLabStartupReaper> logger)
    {
        ArgumentNullException.ThrowIfNull(dataDirectory);
        ArgumentNullException.ThrowIfNull(experiments);
        ArgumentNullException.ThrowIfNull(inspector);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        _dataDirectory = dataDirectory;
        _experiments = experiments;
        _inspector = inspector;
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Warm workers first: their receipt files live beside the work root, not in it, and are deleted only once proven gone.
        await ReapWarmReceiptsAsync(cancellationToken);
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IModelLabExperimentStore>();
            foreach (var entry in await store.ListLaunchReceiptsAsync(cancellationToken))
            {
                _ = await TrainingProcessReaper.ReapOneAsync(_inspector,
                    entry.ExperimentId,
                    entry.LaunchReceiptJson,
                    ct => store.SetLaunchReceiptAsync(entry.ExperimentId, launchReceiptJson: null, ct),
                    _logger,
                    cancellationToken);
            }

            var failed = await store.RecoverOnStartupAsync(cancellationToken);
            if (failed.Count > 0)
            {
                _logger.LogWarning("Marked {Count} Model Lab experiments interrupted by the previous shutdown as failed.", failed.Count);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A sweep failure must never block node startup.
            _logger.LogError(exception, "The Model Lab startup sweep failed.");
        }

        BestEffortFile.TryDeleteDirectory(ModelLabExperimentService.WorkRoot(_dataDirectory));
    }

    public Task StopAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    /// <summary>Reaps the warm receipt files and hands every one not proven gone to the service; a directory that cannot be listed refuses starts instead.</summary>
    private async Task ReapWarmReceiptsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<string> receipts;
        try
        {
            receipts = ModelLabWarmReceipts.List(_dataDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // An absent directory lists as empty; one that exists and cannot be read may name a live worker.
            _logger.LogError(exception, "The Model Lab worker receipts in {Root} could not be listed; Model Lab refuses new starts until they can be.",
                ModelLabWarmReceipts.Root(_dataDirectory));
            _experiments.MarkReceiptScanFailed();
            return;
        }

        var released = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (await ModelLabWarmReceipts.ReapAsync(receipts, static _ => null, _inspector, path => released.Add(path), _logger, cancellationToken) is { } unconfirmed)
            {
                _logger.LogError("A Model Lab worker from the previous run ({Worker}) could not be confirmed stopped; Model Lab refuses new starts until it is.", unconfirmed.Name);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Reaping the Model Lab worker receipts failed; every receipt not proven gone is kept.");
        }
        finally
        {
            // Handed over by name: a start re-reaps only these, never the directory a concurrent start is writing to.
            foreach (var kept in receipts.Where(path => !released.Contains(path)))
            {
                _experiments.AdoptUnresolvedReceipt(kept);
            }
        }
    }
}

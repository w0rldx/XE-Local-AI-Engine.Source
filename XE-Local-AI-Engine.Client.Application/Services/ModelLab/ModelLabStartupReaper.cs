namespace XE_Local_AI_Engine.Client.Services.ModelLab;

using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Training.Runs;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Training.Contracts;

/// <summary>What a previous process left behind: a live job, the rows it was writing, and its plaintext work directory.</summary>
/// <remarks>
///     The work directories go unconditionally, not by age: a job never resumes, and nothing runs before this does. A receipt whose
///     kill failed stays for the next startup, and its row is still failed: the row says the experiment is over, not that the process is.
/// </remarks>
public sealed class ModelLabStartupReaper : IHostedService
{
    private readonly INodeDataDirectory _dataDirectory;
    private readonly ITrainingProcessInspector _inspector;
    private readonly ILogger<ModelLabStartupReaper> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    public ModelLabStartupReaper(IServiceScopeFactory scopeFactory,
        ITrainingProcessInspector inspector,
        INodeDataDirectory dataDirectory,
        ILogger<ModelLabStartupReaper> logger)
    {
        ArgumentNullException.ThrowIfNull(dataDirectory);
        ArgumentNullException.ThrowIfNull(inspector);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        _dataDirectory = dataDirectory;
        _inspector = inspector;
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IModelLabExperimentStore>();
            foreach (var entry in await store.ListLaunchReceiptsAsync(cancellationToken))
            {
                await TrainingProcessReaper.ReapOneAsync(_inspector,
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
}

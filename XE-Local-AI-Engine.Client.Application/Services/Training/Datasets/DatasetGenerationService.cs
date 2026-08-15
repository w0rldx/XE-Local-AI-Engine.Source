namespace XE_Local_AI_Engine.Client.Services.Training.Datasets;

using XE_Local_AI_Engine.Client.Persistence.Stores;

public interface IDatasetGenerationService
{
    /// <summary>
    ///     Creates the dataset and enqueues its single generation work item. Refused with a <c>TrainingBusy</c> conflict
    ///     while a training run holds <see cref="ITrainingActivity" /> (decision #13).
    /// </summary>
    Task<TrainingDatasetRecord> StartAsync(Guid definitionId, long expectedDefinitionVersion, string name, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class DatasetGenerationService(
    ITrainingDatasetStore store,
    ITrainingActivity trainingActivity,
    IDatasetGenerationQueueSignal signal) : IDatasetGenerationService
{
    private readonly IDatasetGenerationQueueSignal _signal = signal ?? throw new ArgumentNullException(nameof(signal));
    private readonly ITrainingDatasetStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly ITrainingActivity _trainingActivity = trainingActivity ?? throw new ArgumentNullException(nameof(trainingActivity));

    public async Task<TrainingDatasetRecord> StartAsync(Guid definitionId,
        long expectedDefinitionVersion,
        string name,
        CancellationToken cancellationToken = default)
    {
        if (_trainingActivity.IsActive)
        {
            throw new TrainingConflictException("TrainingBusy");
        }

        var dataset = await _store.CreateDatasetAndEnqueueAsync(new TrainingDatasetEnqueueCommand(definitionId, expectedDefinitionVersion, name),
                                      cancellationToken)
                                  .ConfigureAwait(false);
        _signal.Wake();
        return dataset;
    }
}

namespace XE_Local_AI_Engine.Client.Services.Training.Datasets;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Training.Runs;

public interface IDatasetGenerationService
{
    /// <summary>
    ///     Creates the dataset and enqueues its single generation work item. Refused with a <c>TrainingBusy</c> conflict
    ///     while something holds <see cref="IGpuWorkGate" /> exclusively (decision #13). That refusal is UX only — the
    ///     enqueue is harmless while a run is active; the QUEUE is what actually enforces exclusivity, at its claim.
    /// </summary>
    Task<TrainingDatasetRecord> StartAsync(Guid definitionId, long expectedDefinitionVersion, string name, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Requests cancellation. A queued dataset is terminalized directly; a generating one is signalled through the
    ///     executor's registry so it can stop cooperatively and still be recorded as cancelled. Returns
    ///     <see langword="false" /> for an unknown dataset or one whose work item is already terminal.
    /// </summary>
    Task<bool> CancelAsync(Guid datasetId, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class DatasetGenerationService(
    ITrainingDatasetStore store,
    IGpuWorkGate gpuWorkGate,
    TrainingRunCancellationRegistry cancellations,
    IDatasetGenerationQueueSignal signal) : IDatasetGenerationService
{
    private readonly TrainingRunCancellationRegistry _cancellations = cancellations ?? throw new ArgumentNullException(nameof(cancellations));
    private readonly IGpuWorkGate _gpuWorkGate = gpuWorkGate ?? throw new ArgumentNullException(nameof(gpuWorkGate));
    private readonly IDatasetGenerationQueueSignal _signal = signal ?? throw new ArgumentNullException(nameof(signal));
    private readonly ITrainingDatasetStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public async Task<TrainingDatasetRecord> StartAsync(Guid definitionId,
        long expectedDefinitionVersion,
        string name,
        CancellationToken cancellationToken = default)
    {
        if (_gpuWorkGate.ExclusiveKind is not null)
        {
            throw new TrainingConflictException("TrainingBusy");
        }

        var dataset = await _store.CreateDatasetAndEnqueueAsync(new TrainingDatasetEnqueueCommand(definitionId, expectedDefinitionVersion, name),
                                      cancellationToken)
                                  .ConfigureAwait(false);
        _signal.Wake();
        return dataset;
    }

    public async Task<bool> CancelAsync(Guid datasetId, CancellationToken cancellationToken = default)
    {
        var dataset = await _store.GetDatasetAsync(datasetId, cancellationToken).ConfigureAwait(false);
        if (dataset?.WorkStatus is not (DatasetGenerationWorkStatus.Queued or DatasetGenerationWorkStatus.Running))
        {
            return false;
        }

        // A running generation is signalled, never terminalized here: the executor owns the cooperative stop and the
        // terminal write, so cancelling from two places would race the work item.
        if (_cancellations.Cancel(datasetId))
        {
            return true;
        }

        _ = await _store.CompleteGenerationAsync(datasetId, DatasetGenerationWorkStatus.Cancelled, "Cancelled before generation started.", cancellationToken)
                        .ConfigureAwait(false);
        return true;
    }
}

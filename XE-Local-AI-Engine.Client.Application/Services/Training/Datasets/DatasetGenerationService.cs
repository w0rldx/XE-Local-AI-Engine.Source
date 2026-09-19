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
public sealed class DatasetGenerationService : IDatasetGenerationService
{
    private readonly TrainingRunCancellationRegistry _cancellations;
    private readonly IGpuWorkGate _gpuWorkGate;
    private readonly IDatasetGenerationQueueSignal _signal;
    private readonly ITrainingDatasetStore _store;

    public DatasetGenerationService(
        ITrainingDatasetStore store,
        IGpuWorkGate gpuWorkGate,
        TrainingRunCancellationRegistry cancellations,
        IDatasetGenerationQueueSignal signal)
    {
        ArgumentNullException.ThrowIfNull(cancellations);
        ArgumentNullException.ThrowIfNull(gpuWorkGate);
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(store);
        _cancellations = cancellations;
        _gpuWorkGate = gpuWorkGate;
        _signal = signal;
        _store = store;
    }

    public async Task<TrainingDatasetRecord> StartAsync(Guid definitionId,
        long expectedDefinitionVersion,
        string name,
        CancellationToken cancellationToken = default)
    {
        if (_gpuWorkGate.ExclusiveKind is not null)
        {
            throw new TrainingConflictException("TrainingBusy");
        }

        var dataset = await _store.CreateDatasetAndEnqueueAsync(new TrainingDatasetEnqueueCommand { DefinitionId = definitionId, ExpectedDefinitionVersion = expectedDefinitionVersion, Name = name },
                                      cancellationToken);
        _signal.Wake();
        return dataset;
    }

    public async Task<bool> CancelAsync(Guid datasetId, CancellationToken cancellationToken = default)
    {
        var dataset = await _store.GetDatasetAsync(datasetId, cancellationToken);
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

        _ = await _store.CompleteGenerationAsync(datasetId, DatasetGenerationWorkStatus.Cancelled, "Cancelled before generation started.", cancellationToken);
        return true;
    }
}

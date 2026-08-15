namespace XE_Local_AI_Engine.Tests.Training;

using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Training;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;
using XE_Local_AI_Engine.Client.Services.Training.Runs;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class DatasetGenerationQueueTests
{
    [Test]
    public async Task Exclusivity_TrainingRunActive_GenerationRefused()
    {
        var gate = new GpuWorkGate();
        var store = Substitute.For<ITrainingDatasetStore>();
        using var startSignal = new DatasetGenerationQueueSignal();
        var service = Service(store, gate, startSignal);
        using var held = AssertEx.NotNull(gate.TryBeginExclusive(GpuWorkKind.TrainingRun), "A run holds the gate exclusively.");

        var exception = await AssertEx.ThrowsAsync<TrainingConflictException>(() => service.StartAsync(Guid.NewGuid(), 1, "dataset"));

        AssertEx.Equal("TrainingBusy", exception.Code);
        _ = store.DidNotReceive().CreateDatasetAndEnqueueAsync(Arg.Any<TrainingDatasetEnqueueCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Exclusivity_AfterTheRunReleases_GenerationEnqueuesAndWakesTheQueue()
    {
        var gate = new GpuWorkGate();
        gate.TryBeginExclusive(GpuWorkKind.TrainingRun)!.Dispose();
        var store = Substitute.For<ITrainingDatasetStore>();
        _ = store.CreateDatasetAndEnqueueAsync(Arg.Any<TrainingDatasetEnqueueCommand>(), Arg.Any<CancellationToken>()).Returns(Dataset());
        using var signal = new DatasetGenerationQueueSignal();
        var service = Service(store, gate, signal);

        _ = await service.StartAsync(Guid.NewGuid(), 1, "dataset");

        _ = await store.Received(1).CreateDatasetAndEnqueueAsync(Arg.Any<TrainingDatasetEnqueueCommand>(), Arg.Any<CancellationToken>());
        AssertEx.True(await signal.WaitAsync(TimeSpan.Zero, CancellationToken.None), "Enqueueing must wake the single consumer.");
    }

    /// <summary>A dataset the queue has not claimed yet has no executor to signal, so the service terminalizes it.</summary>
    [Test]
    public async Task Cancel_AQueuedDataset_TerminalizesItAsCancelled()
    {
        var store = Substitute.For<ITrainingDatasetStore>();
        var datasetId = Guid.NewGuid();
        _ = store.GetDatasetAsync(datasetId, Arg.Any<CancellationToken>()).Returns(Dataset());
        using var signal = new DatasetGenerationQueueSignal();
        var service = Service(store, new GpuWorkGate(), signal);

        AssertEx.True(await service.CancelAsync(datasetId));

        _ = await store.Received(1).CompleteGenerationAsync(datasetId, DatasetGenerationWorkStatus.Cancelled, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Cancel_ARunningDataset_SignalsTheExecutorInsteadOfTerminalizingIt()
    {
        var store = Substitute.For<ITrainingDatasetStore>();
        var datasetId = Guid.NewGuid();
        _ = store.GetDatasetAsync(datasetId, Arg.Any<CancellationToken>())
                 .Returns(Dataset() with
                 {
                     WorkStatus = DatasetGenerationWorkStatus.Running
                 });
        var cancellations = new TrainingRunCancellationRegistry();
        using var source = new CancellationTokenSource();
        using var registration = cancellations.Register(datasetId, source);
        using var signal = new DatasetGenerationQueueSignal();
        var service = Service(store, new GpuWorkGate(), signal, cancellations);

        AssertEx.True(await service.CancelAsync(datasetId));

        AssertEx.True(source.IsCancellationRequested, "A running generation is signalled, not terminalized behind the executor's back.");
        _ = await store.DidNotReceiveWithAnyArgs().CompleteGenerationAsync(Arg.Any<Guid>(), Arg.Any<DatasetGenerationWorkStatus>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Cancel_AnUnknownOrTerminalDataset_ReturnsFalse()
    {
        var store = Substitute.For<ITrainingDatasetStore>();
        var terminal = Guid.NewGuid();
        _ = store.GetDatasetAsync(terminal, Arg.Any<CancellationToken>())
                 .Returns(Dataset() with
                 {
                     WorkStatus = DatasetGenerationWorkStatus.Succeeded
                 });
        using var signal = new DatasetGenerationQueueSignal();
        var service = Service(store, new GpuWorkGate(), signal);

        AssertEx.False(await service.CancelAsync(Guid.NewGuid()), "An unknown dataset cannot be cancelled.");
        AssertEx.False(await service.CancelAsync(terminal), "A finished dataset cannot be cancelled.");
        _ = await store.DidNotReceiveWithAnyArgs().CompleteGenerationAsync(Arg.Any<Guid>(), Arg.Any<DatasetGenerationWorkStatus>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public void QueueSignal_CoalescesRepeatedWakes()
    {
        using var signal = new DatasetGenerationQueueSignal();

        signal.Wake();
        signal.Wake();

        AssertEx.True(signal.WaitAsync(TimeSpan.Zero, CancellationToken.None).GetAwaiter().GetResult());
        AssertEx.False(signal.WaitAsync(TimeSpan.Zero, CancellationToken.None).GetAwaiter().GetResult(),
            "A pending wake is sufficient; the second Wake must not queue a second pass.");
    }

    [Test]
    public void EventBuffer_ReplaysAfterCursorAndDemandsResetOnceEvicted()
    {
        var datasetId = Guid.NewGuid();
        var buffer = new DatasetGenerationEventBuffer(Options.Create(new DatasetGenerationEventBufferOptions()));
        _ = buffer.Append(datasetId, DatasetGenerationEventKind.State, new DatasetGenerationPayload(State: "Generating"));
        var second = buffer.Append(datasetId, DatasetGenerationEventKind.SampleAdded, new DatasetGenerationPayload(Completed: 1, Total: 4));

        var replay = buffer.Replay(datasetId, afterSequence: 1);
        AssertEx.False(replay.ResetRequired);
        AssertEx.Equal(second.Sequence, replay.Events.Single().Sequence);

        buffer.EvictPlaintext(datasetId);
        AssertEx.True(buffer.Replay(datasetId, afterSequence: 1).ResetRequired, "An evicted buffer must ask the client to replay.");
    }

    [Test]
    public void EventBuffer_TrimsToItsBound_AndThenDemandsReset()
    {
        var datasetId = Guid.NewGuid();
        var buffer = new DatasetGenerationEventBuffer(Options.Create(new DatasetGenerationEventBufferOptions
        {
            MaxEventCount = 2
        }));
        for (var index = 0; index < 5; index++)
        {
            _ = buffer.Append(datasetId, DatasetGenerationEventKind.Progress, new DatasetGenerationPayload(Completed: index));
        }

        AssertEx.True(buffer.Replay(datasetId, afterSequence: 0).ResetRequired, "A cursor older than the retained window forces a reset.");
        AssertEx.Equal(expected: 1, buffer.Replay(datasetId, afterSequence: 4).Events.Count);
    }

    private static DatasetGenerationService Service(ITrainingDatasetStore store,
        IGpuWorkGate gate,
        IDatasetGenerationQueueSignal signal,
        TrainingRunCancellationRegistry? cancellations = null) =>
        new(store, gate, cancellations ?? new TrainingRunCancellationRegistry(), signal);

    private static TrainingDatasetRecord Dataset() =>
        new(Guid.NewGuid(), Guid.NewGuid(), 1, "dataset", TrainingDatasetStatus.Generating, 1, null, 0, 0, 0, 0, 0, 1, 0, 0,
            DatasetGenerationWorkStatus.Queued, null);
}

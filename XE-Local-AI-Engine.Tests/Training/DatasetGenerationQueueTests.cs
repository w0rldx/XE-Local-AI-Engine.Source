namespace XE_Local_AI_Engine.Tests.Training;

using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Training;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class DatasetGenerationQueueTests
{
    [Test]
    public async Task Exclusivity_TrainingRunActive_GenerationRefused()
    {
        var activity = new TrainingActivity();
        var store = Substitute.For<ITrainingDatasetStore>();
        using var startSignal = new DatasetGenerationQueueSignal();
        var service = new DatasetGenerationService(store, activity, startSignal);
        using var held = AssertEx.NotNull(activity.TryBegin(), "The first acquisition wins the exclusive flag.");

        var exception = await AssertEx.ThrowsAsync<TrainingConflictException>(() => service.StartAsync(Guid.NewGuid(), 1, "dataset"));

        AssertEx.Equal("TrainingBusy", exception.Code);
        _ = store.DidNotReceive().CreateDatasetAndEnqueueAsync(Arg.Any<TrainingDatasetEnqueueCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Exclusivity_AfterTheRunReleases_GenerationEnqueuesAndWakesTheQueue()
    {
        var activity = new TrainingActivity();
        activity.TryBegin()!.Dispose();
        var store = Substitute.For<ITrainingDatasetStore>();
        _ = store.CreateDatasetAndEnqueueAsync(Arg.Any<TrainingDatasetEnqueueCommand>(), Arg.Any<CancellationToken>()).Returns(Dataset());
        using var signal = new DatasetGenerationQueueSignal();
        var service = new DatasetGenerationService(store, activity, signal);

        _ = await service.StartAsync(Guid.NewGuid(), 1, "dataset");

        _ = await store.Received(1).CreateDatasetAndEnqueueAsync(Arg.Any<TrainingDatasetEnqueueCommand>(), Arg.Any<CancellationToken>());
        AssertEx.True(await signal.WaitAsync(TimeSpan.Zero, CancellationToken.None), "Enqueueing must wake the single consumer.");
    }

    [Test]
    public void TrainingActivity_IsExclusiveAndReleasesOnDispose()
    {
        var activity = new TrainingActivity();
        AssertEx.False(activity.IsActive);

        var first = AssertEx.NotNull(activity.TryBegin());
        AssertEx.True(activity.IsActive);
        AssertEx.Null(activity.TryBegin(), "A second acquisition while held must fail.");

        first.Dispose();
        AssertEx.False(activity.IsActive);
        first.Dispose();
        AssertEx.False(activity.IsActive, "Disposing twice is a no-op, not a release of somebody else's hold.");
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

    private static TrainingDatasetRecord Dataset() =>
        new(Guid.NewGuid(), Guid.NewGuid(), 1, "dataset", TrainingDatasetStatus.Generating, 1, null, 0, 0, 0, 0, 0, 1, 0, 0,
            DatasetGenerationWorkStatus.Queued, null);
}

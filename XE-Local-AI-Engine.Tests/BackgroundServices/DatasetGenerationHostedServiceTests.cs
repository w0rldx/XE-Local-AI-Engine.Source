namespace XE_Local_AI_Engine.Tests.BackgroundServices;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Training;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The single durable dataset-generation consumer, driven directly through <c>ExecuteAsync</c> with a scripted
///     store. The load-bearing assertion is host survival: the CLAIM is a database call that can fail transiently, and
///     an exception escaping <c>ExecuteAsync</c> stops the whole host under the default
///     <c>BackgroundServiceExceptionBehavior.StopHost</c>.
/// </summary>
public sealed class DatasetGenerationHostedServiceTests
{
    [Test]
    public async Task ExecuteAsync_WhenClaimingThrows_LogsAndKeepsConsumingLaterWork()
    {
        using var harness = new Harness();
        var following = Work();
        harness.Enqueue(following);
        harness.FirstClaimBehavior = () => Task.FromException<DatasetGenerationClaimedWork?>(new InvalidOperationException("claim failed"));
        // The failed claim parks on the poll interval; the loop must survive that park and claim again.
        harness.Signal.CancelOnWaitNumber = 2;

        await harness.RunToIdleAsync();

        AssertEx.True(harness.ClaimCount >= 2, "A failed claim must be retried after the poll interval, not end the loop.");
        await harness.Executor.Received(1).ExecuteAsync(following, Arg.Any<CancellationToken>());
        AssertEx.True(harness.Logger.HasEntry(LogLevel.Error, "failed while claiming work"),
            "The queue must report the failed claim rather than dying silently.");
    }

    [Test]
    public async Task ExecuteAsync_WhenClaimingThrowsDuringShutdown_StopsWithoutLoggingAFailure()
    {
        using var harness = new Harness();
        harness.FirstClaimBehavior = () => CancelThenThrowAsync(harness.Cancellation);

        await harness.RunToIdleAsync();

        AssertEx.Equal(expected: 1, harness.ClaimCount);
        AssertEx.False(harness.Logger.HasEntry(LogLevel.Error, "failed while claiming work"),
            "A cancellation during shutdown is not a claim failure.");
    }

    [Test]
    public async Task ExecuteAsync_WhenAnExecutorThrows_LogsAndKeepsConsumingLaterWork()
    {
        using var harness = new Harness();
        var failing = Work();
        var following = Work();
        harness.Enqueue(failing, following);
        harness.Executor.ExecuteAsync(failing, Arg.Any<CancellationToken>())
               .Returns(Task.FromException(new InvalidOperationException("executor terminalization failed")));

        await harness.RunToIdleAsync();

        await harness.Executor.Received(1).ExecuteAsync(following, Arg.Any<CancellationToken>());
        AssertEx.True(harness.Logger.HasEntry(LogLevel.Error, "failed while executing dataset"),
            "The queue must report the escaped executor failure rather than dying silently.");
    }

    private static async Task<DatasetGenerationClaimedWork?> CancelThenThrowAsync(CancellationTokenSource cancellation)
    {
        await cancellation.CancelAsync();
        throw new OperationCanceledException();
    }

    private static DatasetGenerationClaimedWork Work()
    {
        var datasetId = Guid.NewGuid();
        return new DatasetGenerationClaimedWork(QueueSequence: 1,
            datasetId,
            Version: 1,
            new TrainingDatasetRecord(datasetId, Guid.NewGuid(), 1, ReadOnlyMemory<byte>.Empty, "dataset",
                TrainingDatasetStatus.Generating, 1, null, 0, 0, 0, 0, 0, 1, 0, 0,
                DatasetGenerationWorkStatus.Running, null));
    }

    private sealed class Harness : IDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly Queue<DatasetGenerationClaimedWork> _queued = new();

        public Harness()
        {
            Store.RecoverOnStartupAsync(Arg.Any<CancellationToken>())
                 .Returns(_ => Task.FromResult<IReadOnlyList<Guid>>([]));
            Store.ClaimNextAsync(Arg.Any<CancellationToken>())
                 .Returns(_ =>
                 {
                     ClaimCount++;
                     if (FirstClaimBehavior is { } behavior)
                     {
                         FirstClaimBehavior = null;
                         return behavior();
                     }

                     return Task.FromResult(_queued.Count > 0 ? _queued.Dequeue() : null);
                 });

            var services = new ServiceCollection();
            _ = services.AddSingleton(Store);
            _ = services.AddSingleton(Executor);
            _provider = services.BuildServiceProvider();
        }

        public ITrainingDatasetStore Store { get; } = Substitute.For<ITrainingDatasetStore>();
        public IDatasetGenerationExecutor Executor { get; } = Substitute.For<IDatasetGenerationExecutor>();
        public IDatasetGenerationEventBuffer Events { get; } = Substitute.For<IDatasetGenerationEventBuffer>();
        public RecordingLogger<DatasetGenerationHostedService> Logger { get; } = new();
        public StopOnWaitSignal Signal { get; } = new();
        public CancellationTokenSource Cancellation { get; } = new();
        public int ClaimCount { get; private set; }

        /// <summary>Scripts the FIRST claim only; every later claim falls back to the queue.</summary>
        public Func<Task<DatasetGenerationClaimedWork?>>? FirstClaimBehavior { get; set; }

        public void Dispose()
        {
            Cancellation.Dispose();
            _provider.Dispose();
        }

        public void Enqueue(params DatasetGenerationClaimedWork[] work)
        {
            foreach (var item in work)
            {
                _queued.Enqueue(item);
            }
        }

        /// <summary>Drains everything queued and returns once the consumer has parked on an empty queue.</summary>
        public async Task RunToIdleAsync()
        {
            Signal.Cancellation = Cancellation;
            using var service = new DatasetGenerationHostedService(_provider.GetRequiredService<IServiceScopeFactory>(),
                Signal,
                Events,
                new GpuWorkGate(),
                Options.Create(new DatasetGenerationQueueOptions
                {
                    PollInterval = TimeSpan.FromMilliseconds(10)
                }),
                Logger);
            await BackgroundServiceTestHelper.RunExecuteAsync(service, Cancellation.Token);
        }
    }

    /// <summary>
    ///     Stands in for <see cref="DatasetGenerationQueueSignal" />: parking on an empty queue cancels the loop's token
    ///     and returns, so <c>ExecuteAsync</c> falls out of its <c>while</c> instead of waiting on wall-clock time.
    /// </summary>
    private sealed class StopOnWaitSignal : IDatasetGenerationQueueSignal
    {
        public int WaitCount { get; private set; }

        public CancellationTokenSource? Cancellation { get; set; }

        /// <summary>Which park stops the loop. Raised when a test needs the consumer to survive an idle poll first.</summary>
        public int CancelOnWaitNumber { get; set; } = 1;

        public void Wake()
        {
        }

        public async Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            WaitCount++;
            if (Cancellation is { } cancellation && WaitCount >= CancelOnWaitNumber)
            {
                await cancellation.CancelAsync();
            }

            return false;
        }
    }
}

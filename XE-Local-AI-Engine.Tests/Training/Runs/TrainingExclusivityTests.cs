namespace XE_Local_AI_Engine.Tests.Training.Runs;

using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Client.Services.Training;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;
using XE_Local_AI_Engine.Client.Services.Training.Runs;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     A training run holds the whole GPU, so exclusivity has to hold in BOTH directions: nothing else may claim while
///     a run is active, and a run may not start beside work that is already executing.
/// </summary>
/// <remarks>
///     <para>
///         Every refusal is at the CLAIM, never at the executor. Refusing at the executor would terminalize queued work
///         as failed, and each of these queues pins its work items to attempt 1 — there is no retry to recover with.
///     </para>
///     <para>
///         The race the gate exists to close is proven structurally, not by timing: the losing side is parked on an
///         external <see cref="TaskCompletionSource" /> INSIDE its held window, so the window is open for as long as
///         the test needs rather than for as long as a sleep happens to last.
///     </para>
/// </remarks>
public sealed class TrainingExclusivityTests
{
    private static readonly TimeSpan BoundedWait = TimeSpan.FromSeconds(5);

    [Test]
    public async Task Exclusivity_RunActive_BenchmarkAndGenerationRefused()
    {
        var gate = new ObservedGpuWorkGate(new GpuWorkGate());
        using var held = AssertEx.NotNull(gate.TryBeginExclusive(GpuWorkKind.TrainingRun), "The run acquires the gate exclusively.");

        var benchmarks = Substitute.For<IBenchmarkStore>();
        var datasets = Substitute.For<ITrainingDatasetStore>();
        var benchmarkAsked = gate.Asked(GpuWorkKind.Benchmark);
        var generationAsked = gate.Asked(GpuWorkKind.DatasetGeneration);
        using var benchmarkSignal = new BenchmarkQueueSignal();
        using var generationSignal = new DatasetGenerationQueueSignal();
        using var benchmarkQueue = BuildBenchmarkQueue(benchmarks, benchmarkSignal, gate);
        using var generationQueue = BuildGenerationQueue(datasets, generationSignal, gate);

        await RunUntilAsync(Task.WhenAll(benchmarkAsked, generationAsked), benchmarkQueue, generationQueue);

        AssertEx.False(await benchmarkAsked, "The benchmark queue must have been refused at the gate.");
        AssertEx.False(await generationAsked, "Dataset generation must have been refused at the gate.");
        _ = await benchmarks.DidNotReceiveWithAnyArgs().ClaimNextAsync(default);
        _ = await datasets.DidNotReceiveWithAnyArgs().ClaimNextAsync(default);
    }

    [Test]
    public async Task Exclusivity_WhenNoRunIsActive_BenchmarkAndGenerationClaimAsUsual()
    {
        var gate = new GpuWorkGate();
        var benchmarks = Substitute.For<IBenchmarkStore>();
        var datasets = Substitute.For<ITrainingDatasetStore>();
        var benchmarkClaimed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var generationClaimed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = benchmarks.ClaimNextAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            benchmarkClaimed.TrySetResult();
            return Task.FromResult<BenchmarkClaimedWork?>(null);
        });
        _ = datasets.ClaimNextAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            generationClaimed.TrySetResult();
            return Task.FromResult<DatasetGenerationClaimedWork?>(null);
        });
        using var benchmarkSignal = new BenchmarkQueueSignal();
        using var generationSignal = new DatasetGenerationQueueSignal();
        using var benchmarkQueue = BuildBenchmarkQueue(benchmarks, benchmarkSignal, gate);
        using var generationQueue = BuildGenerationQueue(datasets, generationSignal, gate);

        await RunUntilAsync(Task.WhenAll(benchmarkClaimed.Task, generationClaimed.Task), benchmarkQueue, generationQueue);

        // The guard is a refusal while an exclusive holder owns the node, not a permanent stop. Both are SHARED, so
        // they also have to be able to run beside each other.
        _ = await benchmarks.ReceivedWithAnyArgs().ClaimNextAsync(default);
        _ = await datasets.ReceivedWithAnyArgs().ClaimNextAsync(default);
    }

    /// <summary>
    ///     The race the old design lost: dataset generation that is genuinely EXECUTING — gate held, executor parked
    ///     on an external signal — while the run queue polls. Under the old check-then-act pair (a status sweep, then a
    ///     separate flag) the run could pass the sweep in the window before the other side flipped anything, and admit
    ///     onto a GPU that was already in use. There is one lock now, and taking it IS the check.
    /// </summary>
    [Test]
    public async Task Exclusivity_RunStartRefusedWhileGenerationIsMidExecution_AndAdmitsOnceItReleases()
    {
        var gate = new ObservedGpuWorkGate(new GpuWorkGate());
        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var datasets = Substitute.For<ITrainingDatasetStore>();
        _ = datasets.RecoverOnStartupAsync(Arg.Any<CancellationToken>()).Returns<IReadOnlyList<Guid>>([]);
        var claims = 0;
        _ = datasets.ClaimNextAsync(Arg.Any<CancellationToken>())
                    .Returns(_ => Task.FromResult(Interlocked.Increment(ref claims) == 1 ? Work() : null));

        var generationExecutor = Substitute.For<IDatasetGenerationExecutor>();
        _ = generationExecutor.ExecuteAsync(Arg.Any<DatasetGenerationClaimedWork>(), Arg.Any<CancellationToken>())
                              .Returns(_ =>
                              {
                                  executing.TrySetResult();
                                  return parked.Task;
                              });

        var runs = Substitute.For<ITrainingRunStore>();
        _ = runs.RecoverOnStartupAsync(Arg.Any<CancellationToken>()).Returns<IReadOnlyList<Guid>>([]);
        _ = runs.PeekNextKindAsync(Arg.Any<CancellationToken>()).Returns(TrainingWorkKind.TrainingRun);
        var claimed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = runs.ClaimNextAsync(TrainingWorkKind.TrainingRun, Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    claimed.TrySetResult();
                    return Task.FromResult<TrainingWorkClaim?>(null);
                });

        using var generationSignal = new DatasetGenerationQueueSignal();
        using var generationQueue = BuildGenerationQueue(datasets, generationSignal, gate, generationExecutor);
        await generationQueue.StartAsync(CancellationToken.None);
        try
        {
            await executing.Task.WaitAsync(BoundedWait);

            var runAsked = gate.Asked(GpuWorkKind.TrainingRun);
            using (var refused = BuildRunQueue(runs, gate))
            {
                await RunUntilAsync(runAsked, refused);
            }

            AssertEx.False(await runAsked, "The run queue must have been refused at the gate generation was holding.");
            _ = await runs.DidNotReceiveWithAnyArgs().ClaimNextAsync(default(TrainingWorkKind), default);
            AssertEx.Null(gate.ExclusiveKind, "A refused run must not have left an exclusive hold behind.");
        }
        finally
        {
            // Releasing the parked executor lets the generation queue finish its iteration and drop the shared hold.
            parked.TrySetResult();
            await generationQueue.StopAsync(CancellationToken.None);
        }

        // The refusal was the hold, not a permanent stop: with the gate free the same run queue claims.
        using var admitted = BuildRunQueue(runs, gate);
        await admitted.StartAsync(CancellationToken.None);
        try
        {
            await claimed.Task.WaitAsync(BoundedWait);
        }
        finally
        {
            await admitted.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>The mirror: an exclusive holder mid-execution refuses both shared queues at their claim.</summary>
    [Test]
    public async Task Exclusivity_BenchmarkAndGenerationRefusedWhileARunIsMidExecution_AndClaimOnceItReleases()
    {
        var gate = new ObservedGpuWorkGate(new GpuWorkGate());
        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var runs = Substitute.For<ITrainingRunStore>();
        _ = runs.RecoverOnStartupAsync(Arg.Any<CancellationToken>()).Returns<IReadOnlyList<Guid>>([]);
        _ = runs.PeekNextKindAsync(Arg.Any<CancellationToken>()).Returns(TrainingWorkKind.TrainingRun);
        var claim = new TrainingWorkClaim(1, TrainingWorkKind.TrainingRun, Guid.NewGuid(), 1, null);
        var claims = 0;
        _ = runs.ClaimNextAsync(TrainingWorkKind.TrainingRun, Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromResult(Interlocked.Increment(ref claims) == 1 ? claim : null));

        var runExecutor = Substitute.For<ITrainingRunExecutor>();
        _ = runExecutor.ExecuteAsync(Arg.Any<TrainingWorkClaim>(), Arg.Any<CancellationToken>())
                       .Returns(_ =>
                       {
                           executing.TrySetResult();
                           return parked.Task;
                       });

        var benchmarks = Substitute.For<IBenchmarkStore>();
        var datasets = Substitute.For<ITrainingDatasetStore>();
        var claimedAfterRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = datasets.ClaimNextAsync(Arg.Any<CancellationToken>())
                    .Returns(_ =>
                    {
                        claimedAfterRelease.TrySetResult();
                        return Task.FromResult<DatasetGenerationClaimedWork?>(null);
                    });

        using var runQueue = BuildRunQueue(runs, gate, runExecutor);
        await runQueue.StartAsync(CancellationToken.None);
        try
        {
            await executing.Task.WaitAsync(BoundedWait);

            var benchmarkAsked = gate.Asked(GpuWorkKind.Benchmark);
            var generationAsked = gate.Asked(GpuWorkKind.DatasetGeneration);
            using var benchmarkSignal = new BenchmarkQueueSignal();
            using var generationSignal = new DatasetGenerationQueueSignal();
            using (var benchmarkQueue = BuildBenchmarkQueue(benchmarks, benchmarkSignal, gate))
                using (var generationQueue = BuildGenerationQueue(datasets, generationSignal, gate))
                {
                    await RunUntilAsync(Task.WhenAll(benchmarkAsked, generationAsked), benchmarkQueue, generationQueue);
                }

            AssertEx.False(await benchmarkAsked, "The benchmark queue must have been refused at the gate the run was holding.");
            AssertEx.False(await generationAsked, "Dataset generation must have been refused at the gate the run was holding.");
            _ = await benchmarks.DidNotReceiveWithAnyArgs().ClaimNextAsync(default);
            _ = await datasets.DidNotReceiveWithAnyArgs().ClaimNextAsync(default);
        }
        finally
        {
            parked.TrySetResult();
            await runQueue.StopAsync(CancellationToken.None);
        }

        using var freeSignal = new DatasetGenerationQueueSignal();
        using var freeQueue = BuildGenerationQueue(datasets, freeSignal, gate);
        await freeQueue.StartAsync(CancellationToken.None);
        try
        {
            await claimedAfterRelease.Task.WaitAsync(BoundedWait);
        }
        finally
        {
            await freeQueue.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task Exclusivity_RunStartRefusedWhileTheGateIsAlreadyHeld()
    {
        var gate = new GpuWorkGate();
        using var held = AssertEx.NotNull(gate.TryBeginExclusive(GpuWorkKind.Export), "Something else already holds the gate.");
        var runs = Substitute.For<ITrainingRunStore>();
        _ = runs.RecoverOnStartupAsync(Arg.Any<CancellationToken>()).Returns<IReadOnlyList<Guid>>([]);
        var peeked = PeekSignal(runs, TrainingWorkKind.TrainingRun);

        using var queue = BuildRunQueue(runs, gate);
        await RunUntilAsync(peeked.Task, queue);

        _ = await runs.DidNotReceiveWithAnyArgs().ClaimNextAsync(default(TrainingWorkKind), default);
    }

    [Test]
    public async Task Exclusivity_RunStartRefusedWhileTheRuntimeMutationLeaseIsRefused()
    {
        var runs = Substitute.For<ITrainingRunStore>();
        _ = runs.RecoverOnStartupAsync(Arg.Any<CancellationToken>()).Returns<IReadOnlyList<Guid>>([]);
        var peeked = PeekSignal(runs, TrainingWorkKind.TrainingRun);
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        // The lease refuses while ANY llama-server process is running or spawning — the eject-first gate.
        _ = supervisor.TryAcquireRuntimeMutationLeaseAsync(Arg.Any<CancellationToken>()).Returns((ILlamaServerRuntimeMutationLease?)null);

        using var queue = BuildRunQueue(runs, new GpuWorkGate(), supervisor: supervisor);
        await RunUntilAsync(peeked.Task, queue);

        _ = await runs.DidNotReceiveWithAnyArgs().ClaimNextAsync(default(TrainingWorkKind), default);
    }

    [Test]
    public async Task Exclusivity_RunStartClaimsWhenTheBoxIsIdle()
    {
        var runs = Substitute.For<ITrainingRunStore>();
        _ = runs.RecoverOnStartupAsync(Arg.Any<CancellationToken>()).Returns<IReadOnlyList<Guid>>([]);
        _ = runs.PeekNextKindAsync(Arg.Any<CancellationToken>()).Returns(TrainingWorkKind.TrainingRun);
        var claimed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = runs.ClaimNextAsync(TrainingWorkKind.TrainingRun, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            claimed.TrySetResult();
            return Task.FromResult<TrainingWorkClaim?>(null);
        });

        using var queue = BuildRunQueue(runs, new GpuWorkGate());
        await RunUntilAsync(claimed.Task, queue);

        _ = await runs.Received().ClaimNextAsync(TrainingWorkKind.TrainingRun, Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     The evaluation split. An evaluation loads a model through the ordinary chat path, and the runtime-mutation
    ///     lease forbids exactly that, so the evaluation branch takes the gate and NOT the lease. Taking the lease
    ///     would deadlock an evaluation against its own model load.
    /// </summary>
    [Test]
    public async Task Exclusivity_EvaluationClaimsWithoutTheRuntimeMutationLease()
    {
        var runs = Substitute.For<ITrainingRunStore>();
        _ = runs.RecoverOnStartupAsync(Arg.Any<CancellationToken>()).Returns<IReadOnlyList<Guid>>([]);
        _ = runs.PeekNextKindAsync(Arg.Any<CancellationToken>()).Returns(TrainingWorkKind.EvaluationRun);
        var claimed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = runs.ClaimNextAsync(TrainingWorkKind.EvaluationRun, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            claimed.TrySetResult();
            return Task.FromResult<TrainingWorkClaim?>(null);
        });
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        _ = supervisor.TryAcquireRuntimeMutationLeaseAsync(Arg.Any<CancellationToken>())
                      .Returns(Substitute.For<ILlamaServerRuntimeMutationLease>());

        using var queue = BuildRunQueue(runs, new GpuWorkGate(), supervisor: supervisor);
        await RunUntilAsync(claimed.Task, queue);

        _ = await runs.Received().ClaimNextAsync(TrainingWorkKind.EvaluationRun, Arg.Any<CancellationToken>());
        _ = await supervisor.DidNotReceiveWithAnyArgs().TryAcquireRuntimeMutationLeaseAsync(default);
    }

    [Test]
    public async Task Exclusivity_EvaluationStartRefusedWhileTheGateIsAlreadyHeld()
    {
        var gate = new GpuWorkGate();
        using var held = AssertEx.NotNull(gate.TryBeginExclusive(GpuWorkKind.TrainingRun), "A training run already holds the gate.");
        var runs = Substitute.For<ITrainingRunStore>();
        _ = runs.RecoverOnStartupAsync(Arg.Any<CancellationToken>()).Returns<IReadOnlyList<Guid>>([]);
        var peeked = PeekSignal(runs, TrainingWorkKind.EvaluationRun);

        using var queue = BuildRunQueue(runs, gate);
        await RunUntilAsync(peeked.Task, queue);

        // Both kinds take the same exclusive hold, so an evaluation cannot start beside a run either.
        _ = await runs.DidNotReceiveWithAnyArgs().ClaimNextAsync(default(TrainingWorkKind), default);
    }

    [Test]
    public async Task Exclusivity_GateIsNotHeldWhenTheQueueFindsNothingToDo()
    {
        var gate = new GpuWorkGate();
        var runs = Substitute.For<ITrainingRunStore>();
        _ = runs.RecoverOnStartupAsync(Arg.Any<CancellationToken>()).Returns<IReadOnlyList<Guid>>([]);
        var peeked = PeekSignal(runs, kind: null);

        using var queue = BuildRunQueue(runs, gate);
        await RunUntilAsync(peeked.Task, queue);

        // Holding the gate across the idle poll would starve generation, benchmarks and image jobs on a quiet node.
        AssertEx.Null(gate.ExclusiveKind, "An empty queue must not leave the gate held.");
    }

    /// <summary>One claimed generation work item; only its identity matters to the queue under test.</summary>
    private static DatasetGenerationClaimedWork Work() =>
        new(1,
            Guid.NewGuid(),
            1,
            new TrainingDatasetRecord(Guid.NewGuid(), Guid.NewGuid(), 1, null, "dataset", TrainingDatasetStatus.Generating, 1, null, 0, 0, 0, 0, 0, 1, 0, 0,
                DatasetGenerationWorkStatus.Running, null));

    private static BenchmarkQueueHostedService BuildBenchmarkQueue(IBenchmarkStore store, IBenchmarkQueueSignal signal, IGpuWorkGate gate)
    {
        var scopeFactory = ScopeFactory(services =>
        {
            _ = services.AddScoped(_ => store);
            _ = services.AddScoped(_ => Substitute.For<IBenchmarkRunExecutor>());
            _ = services.AddScoped(_ => Substitute.For<IBenchmarkJudgeExecutor>());
        });
        return new BenchmarkQueueHostedService(scopeFactory,
            signal,
            Substitute.For<IBenchmarkEventBuffer>(),
            gate,
            Options.Create(new BenchmarkQueueOptions()),
            NullLogger<BenchmarkQueueHostedService>.Instance);
    }

    private static DatasetGenerationHostedService BuildGenerationQueue(ITrainingDatasetStore store,
        IDatasetGenerationQueueSignal signal,
        IGpuWorkGate gate,
        IDatasetGenerationExecutor? executor = null)
    {
        var scopeFactory = ScopeFactory(services =>
        {
            _ = services.AddScoped(_ => store);
            _ = services.AddScoped(_ => executor ?? Substitute.For<IDatasetGenerationExecutor>());
        });
        return new DatasetGenerationHostedService(scopeFactory,
            signal,
            Substitute.For<IDatasetGenerationEventBuffer>(),
            gate,
            Options.Create(new DatasetGenerationQueueOptions()),
            NullLogger<DatasetGenerationHostedService>.Instance);
    }

    private static TrainingRunQueueHostedService BuildRunQueue(ITrainingRunStore runs,
        IGpuWorkGate gate,
        ITrainingRunExecutor? executor = null,
        ILlamaServerProcessSupervisor? supervisor = null)
    {
        supervisor ??= Substitute.For<ILlamaServerProcessSupervisor>();
        // Owned by the returned service, which the caller disposes with a using.
#pragma warning disable CA2000
        var signal = new TrainingRunQueueSignal();
#pragma warning restore CA2000
        if (supervisor.TryAcquireRuntimeMutationLeaseAsync(Arg.Any<CancellationToken>()) is null)
        {
            _ = supervisor.TryAcquireRuntimeMutationLeaseAsync(Arg.Any<CancellationToken>())
                          .Returns(Substitute.For<ILlamaServerRuntimeMutationLease>());
        }

        var scopeFactory = ScopeFactory(services =>
        {
            _ = services.AddScoped(_ => runs);
            _ = services.AddScoped(_ => executor ?? Substitute.For<ITrainingRunExecutor>());
        });
        return new TrainingRunQueueHostedService(scopeFactory,
            signal,
            Substitute.For<ITrainingRunEventBuffer>(),
            gate,
            supervisor,
            Options.Create(new TrainingRunQueueOptions()),
            NullLogger<TrainingRunQueueHostedService>.Instance);
    }

    private static IServiceScopeFactory ScopeFactory(Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        configure(services);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    /// <summary>
    ///     Answers <paramref name="kind" /> from the run store's peek, and completes when the loop asks for it. The
    ///     peek is the FIRST thing the run loop does and every exclusivity decision is made on the way back from it, so
    ///     this is the signal a refusal assertion needs: without it, "no claim happened" is also what a loop that never
    ///     ran looks like.
    /// </summary>
    private static TaskCompletionSource PeekSignal(ITrainingRunStore runs, TrainingWorkKind? kind)
    {
        var peeked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = runs.PeekNextKindAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            peeked.TrySetResult();
            return kind;
        });

        return peeked;
    }

    /// <summary>
    ///     Starts each queue, waits for <paramref name="reached" /> — a completion the queue's own collaborator sets
    ///     when the loop gets where the test is asserting it gets — and stops them again. There is no unsignalled
    ///     variant on purpose: stopping a queue that had only been settled cancels the loop wherever it happens to be,
    ///     and every negative assertion after that passes on a loop that never reached the gate at all.
    /// </summary>
    private static async Task RunUntilAsync(Task reached, params BackgroundService[] queues)
    {
        foreach (var queue in queues)
        {
            await queue.StartAsync(CancellationToken.None);
        }

        await AssertEx.CompletesAsync(reached, BoundedWait, "The queue loop never reached the claim under test.");

        foreach (var queue in queues)
        {
            await queue.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    ///     The real gate, plus one completion per <see cref="GpuWorkKind" /> that fires when a queue loop first ASKS
    ///     for that kind, carrying the answer it got. That ask is the observable iteration boundary a refusal
    ///     assertion needs: "the queue did not claim" is also true of a loop that was cancelled before it ever
    ///     consulted the gate.
    /// </summary>
    private sealed class ObservedGpuWorkGate(IGpuWorkGate inner) : IGpuWorkGate
    {
        private readonly ConcurrentDictionary<GpuWorkKind, TaskCompletionSource<bool>> _asks = new();

        public GpuWorkKind? ExclusiveKind => inner.ExclusiveKind;

        public IDisposable? TryBeginExclusive(GpuWorkKind kind) => Record(kind, inner.TryBeginExclusive(kind));

        public IDisposable? TryBeginShared(GpuWorkKind kind) => Record(kind, inner.TryBeginShared(kind));

        /// <summary>Completes with the answer the FIRST admission attempt for <paramref name="kind" /> got: true = admitted.</summary>
        public Task<bool> Asked(GpuWorkKind kind) => Ask(kind).Task;

        private IDisposable? Record(GpuWorkKind kind, IDisposable? admission)
        {
            _ = Ask(kind).TrySetResult(admission is not null);
            return admission;
        }

        private TaskCompletionSource<bool> Ask(GpuWorkKind kind) =>
            _asks.GetOrAdd(kind, static _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
    }
}

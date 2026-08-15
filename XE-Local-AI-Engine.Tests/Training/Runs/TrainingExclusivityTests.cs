namespace XE_Local_AI_Engine.Tests.Training.Runs;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Client.Services.Images;
using XE_Local_AI_Engine.Client.Services.Training;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;
using XE_Local_AI_Engine.Client.Services.Training.Runs;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     A training run holds the whole GPU, so exclusivity has to hold in BOTH directions: nothing else may claim while
///     a run is active, and a run may not start behind work that is already in flight.
/// </summary>
/// <remarks>
///     Every refusal is at the CLAIM, never at the executor. Refusing at the executor would terminalize queued work as
///     failed, and each of these queues pins its work items to attempt 1 — there is no retry to recover with.
/// </remarks>
public sealed class TrainingExclusivityTests
{
    private static readonly TimeSpan ObservationWindow = TimeSpan.FromMilliseconds(300);

    [Test]
    public async Task Exclusivity_RunActive_BenchmarkAndGenerationRefused()
    {
        var activity = new TrainingActivity();
        using var held = AssertEx.NotNull(activity.TryBegin(), "The run acquires the exclusivity flag.");

        var benchmarks = Substitute.For<IBenchmarkStore>();
        var datasets = Substitute.For<ITrainingDatasetStore>();
        using var benchmarkSignal = new BenchmarkQueueSignal();
        using var generationSignal = new DatasetGenerationQueueSignal();
        using var benchmarkQueue = new BenchmarkQueueHostedService(ScopeFactory(services => services.AddScoped(_ => benchmarks)),
            benchmarkSignal,
            Substitute.For<IBenchmarkEventBuffer>(),
            activity,
            Options.Create(new BenchmarkQueueOptions()),
            NullLogger<BenchmarkQueueHostedService>.Instance);
        using var generationQueue = new DatasetGenerationHostedService(ScopeFactory(services => services.AddScoped(_ => datasets)),
            generationSignal,
            Substitute.For<IDatasetGenerationEventBuffer>(),
            activity,
            Options.Create(new DatasetGenerationQueueOptions()),
            NullLogger<DatasetGenerationHostedService>.Instance);

        await RunBrieflyAsync(benchmarkQueue, generationQueue);

        _ = await benchmarks.DidNotReceiveWithAnyArgs().ClaimNextAsync(default);
        _ = await datasets.DidNotReceiveWithAnyArgs().ClaimNextAsync(default);
    }

    [Test]
    public async Task Exclusivity_WhenNoRunIsActive_BenchmarkAndGenerationClaimAsUsual()
    {
        var activity = new TrainingActivity();
        var benchmarks = Substitute.For<IBenchmarkStore>();
        var datasets = Substitute.For<ITrainingDatasetStore>();
        using var benchmarkSignal = new BenchmarkQueueSignal();
        using var generationSignal = new DatasetGenerationQueueSignal();
        using var benchmarkQueue = new BenchmarkQueueHostedService(ScopeFactory(services => services.AddScoped(_ => benchmarks)),
            benchmarkSignal,
            Substitute.For<IBenchmarkEventBuffer>(),
            activity,
            Options.Create(new BenchmarkQueueOptions()),
            NullLogger<BenchmarkQueueHostedService>.Instance);
        using var generationQueue = new DatasetGenerationHostedService(ScopeFactory(services => services.AddScoped(_ => datasets)),
            generationSignal,
            Substitute.For<IDatasetGenerationEventBuffer>(),
            activity,
            Options.Create(new DatasetGenerationQueueOptions()),
            NullLogger<DatasetGenerationHostedService>.Instance);

        await RunBrieflyAsync(benchmarkQueue, generationQueue);

        // The guard is a refusal while a run holds the flag, not a permanent stop.
        _ = await benchmarks.ReceivedWithAnyArgs().ClaimNextAsync(default);
        _ = await datasets.ReceivedWithAnyArgs().ClaimNextAsync(default);
    }

    [Test]
    public async Task Exclusivity_ConverseRunStartRefusedWhileOtherGpuWorkIsActive()
    {
        foreach (var (label, imageActive, benchmarkActive, generationActive) in Scenarios())
        {
            var runs = Substitute.For<ITrainingRunStore>();
            _ = runs.RecoverOnStartupAsync(Arg.Any<CancellationToken>()).Returns<IReadOnlyList<Guid>>([]);
            var benchmarks = Substitute.For<IBenchmarkStore>();
            _ = benchmarks.HasActiveWorkAsync(Arg.Any<CancellationToken>()).Returns(benchmarkActive);
            var datasets = Substitute.For<ITrainingDatasetStore>();
            _ = datasets.HasActiveGenerationAsync(Arg.Any<CancellationToken>()).Returns(generationActive);
            var images = Substitute.For<IImageJobCoordinator>();
            _ = images.HasActiveJob.Returns(imageActive);

            using var queue = BuildRunQueue(runs, benchmarks, datasets, images, new TrainingActivity());
            await RunBrieflyAsync(queue);

            _ = await runs.DidNotReceiveWithAnyArgs().ClaimNextAsync(default);
            AssertEx.True(condition: true, label);
        }
    }

    [Test]
    public async Task Exclusivity_RunStartRefusedWhileTheActivityFlagIsAlreadyHeld()
    {
        var activity = new TrainingActivity();
        using var held = AssertEx.NotNull(activity.TryBegin(), "Something else already holds the flag.");
        var runs = Substitute.For<ITrainingRunStore>();
        _ = runs.RecoverOnStartupAsync(Arg.Any<CancellationToken>()).Returns<IReadOnlyList<Guid>>([]);

        using var queue = BuildRunQueue(runs, IdleBenchmarks(), IdleDatasets(), IdleImages(), activity);
        await RunBrieflyAsync(queue);

        _ = await runs.DidNotReceiveWithAnyArgs().ClaimNextAsync(default);
    }

    [Test]
    public async Task Exclusivity_RunStartRefusedWhileTheRuntimeMutationLeaseIsRefused()
    {
        var runs = Substitute.For<ITrainingRunStore>();
        _ = runs.RecoverOnStartupAsync(Arg.Any<CancellationToken>()).Returns<IReadOnlyList<Guid>>([]);
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        // The lease refuses while ANY llama-server process is running or spawning — the eject-first gate.
        _ = supervisor.TryAcquireRuntimeMutationLeaseAsync(Arg.Any<CancellationToken>()).Returns((ILlamaServerRuntimeMutationLease?)null);

        using var queue = BuildRunQueue(runs, IdleBenchmarks(), IdleDatasets(), IdleImages(), new TrainingActivity(), supervisor);
        await RunBrieflyAsync(queue);

        _ = await runs.DidNotReceiveWithAnyArgs().ClaimNextAsync(default);
    }

    [Test]
    public async Task Exclusivity_RunStartClaimsWhenTheBoxIsIdle()
    {
        var runs = Substitute.For<ITrainingRunStore>();
        _ = runs.RecoverOnStartupAsync(Arg.Any<CancellationToken>()).Returns<IReadOnlyList<Guid>>([]);
        _ = runs.ClaimNextAsync(Arg.Any<CancellationToken>()).Returns((TrainingWorkClaim?)null);

        using var queue = BuildRunQueue(runs, IdleBenchmarks(), IdleDatasets(), IdleImages(), new TrainingActivity());
        await RunBrieflyAsync(queue);

        _ = await runs.ReceivedWithAnyArgs().ClaimNextAsync(default);
    }

    [Test]
    public async Task Exclusivity_ActivityFlagIsReleasedWhenTheQueueFindsNothingToDo()
    {
        var activity = new TrainingActivity();
        var runs = Substitute.For<ITrainingRunStore>();
        _ = runs.RecoverOnStartupAsync(Arg.Any<CancellationToken>()).Returns<IReadOnlyList<Guid>>([]);
        _ = runs.ClaimNextAsync(Arg.Any<CancellationToken>()).Returns((TrainingWorkClaim?)null);

        using var queue = BuildRunQueue(runs, IdleBenchmarks(), IdleDatasets(), IdleImages(), activity);
        await RunBrieflyAsync(queue);

        // Holding the flag across the idle poll would starve generation and benchmarks forever on a quiet node.
        AssertEx.False(activity.IsActive, "An empty queue must not leave the exclusivity flag held.");
    }

    private static IEnumerable<(string Label, bool Image, bool Benchmark, bool Generation)> Scenarios() =>
    [
        ("an image job is generating", true, false, false),
        ("a benchmark is queued or running", false, true, false),
        ("dataset generation is in flight", false, false, true)
    ];

    private static IBenchmarkStore IdleBenchmarks()
    {
        var store = Substitute.For<IBenchmarkStore>();
        _ = store.HasActiveWorkAsync(Arg.Any<CancellationToken>()).Returns(false);
        return store;
    }

    private static ITrainingDatasetStore IdleDatasets()
    {
        var store = Substitute.For<ITrainingDatasetStore>();
        _ = store.HasActiveGenerationAsync(Arg.Any<CancellationToken>()).Returns(false);
        return store;
    }

    private static IImageJobCoordinator IdleImages()
    {
        var coordinator = Substitute.For<IImageJobCoordinator>();
        _ = coordinator.HasActiveJob.Returns(false);
        return coordinator;
    }

    private static TrainingRunQueueHostedService BuildRunQueue(ITrainingRunStore runs,
        IBenchmarkStore benchmarks,
        ITrainingDatasetStore datasets,
        IImageJobCoordinator images,
        ITrainingActivity activity,
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
            _ = services.AddScoped(_ => benchmarks);
            _ = services.AddScoped(_ => datasets);
        });
        return new TrainingRunQueueHostedService(scopeFactory,
            signal,
            Substitute.For<ITrainingRunEventBuffer>(),
            activity,
            supervisor,
            images,
            Options.Create(new TrainingRunQueueOptions()),
            NullLogger<TrainingRunQueueHostedService>.Instance);
    }

    private static IServiceScopeFactory ScopeFactory(Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        configure(services);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    /// <summary>Starts each queue, lets it take a few passes at its loop, and stops it again.</summary>
    private static async Task RunBrieflyAsync(params BackgroundService[] queues)
    {
        foreach (var queue in queues)
        {
            await queue.StartAsync(CancellationToken.None);
        }

        await Task.Delay(ObservationWindow);

        foreach (var queue in queues)
        {
            await queue.StopAsync(CancellationToken.None);
        }
    }
}

namespace XE_Local_AI_Engine.Tests.BackgroundServices;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Unit tests for the single durable benchmark-queue consumer. The loop is driven directly through
///     <c>ExecuteAsync</c> with a scripted store, so the assertions are about the consumer's contract: it recovers
///     interrupted runs (and evicts their plaintext) BEFORE claiming anything, routes each claim to the executor that
///     matches its kind, and — the load-bearing one — stays alive when an executor's own failure handling throws, since
///     a dead consumer silently starves every later durable run.
/// </summary>
public sealed class BenchmarkQueueHostedServiceTests
{
    [Test]
    public void Constructor_WhenPollIntervalIsNotPositive_Throws()
    {
        using var harness = new Harness();

        _ = AssertEx.Throws<InvalidOperationException>(() => harness.CreateService(TimeSpan.Zero));
        _ = AssertEx.Throws<InvalidOperationException>(() => harness.CreateService(TimeSpan.FromSeconds(-1)));
    }

    [Test]
    public async Task ExecuteAsync_OnStartup_RecoversInterruptedRunsAndEvictsTheirPlaintext()
    {
        using var harness = new Harness();
        var firstRunId = Guid.NewGuid();
        var secondRunId = Guid.NewGuid();
        harness.Recovered = [Run(firstRunId), Run(secondRunId)];

        await harness.RunToIdleAsync();

        _ = await harness.Store.Received(1).RecoverRunsOnStartupAsync(Arg.Any<CancellationToken>());
        harness.Events.Received(1).EvictPlaintext(firstRunId);
        harness.Events.Received(1).EvictPlaintext(secondRunId);
    }

    [Test]
    public async Task ExecuteAsync_WhenNothingIsQueued_WaitsOnTheSignalInsteadOfSpinning()
    {
        using var harness = new Harness();

        await harness.RunToIdleAsync();

        AssertEx.Equal(expected: 1, harness.Signal.WaitCount);
        await harness.RunExecutor.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default);
        await harness.JudgeExecutor.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default);
    }

    [Test]
    public async Task ExecuteAsync_RoutesPrimaryWorkToTheRunExecutorAndJudgeWorkToTheJudgeExecutor()
    {
        using var harness = new Harness();
        var primary = Work(BenchmarkWorkKind.Primary);
        var judge = Work(BenchmarkWorkKind.Judge);
        harness.Enqueue(primary, judge);

        await harness.RunToIdleAsync();

        await harness.RunExecutor.Received(1).ExecuteAsync(primary, Arg.Any<CancellationToken>());
        await harness.RunExecutor.DidNotReceive().ExecuteAsync(judge, Arg.Any<CancellationToken>());
        await harness.JudgeExecutor.Received(1).ExecuteAsync(judge, Arg.Any<CancellationToken>());
        await harness.JudgeExecutor.DidNotReceive().ExecuteAsync(primary, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecuteAsync_WhenAnExecutorThrows_LogsAndKeepsConsumingLaterWork()
    {
        using var harness = new Harness();
        var failing = Work(BenchmarkWorkKind.Primary);
        var following = Work(BenchmarkWorkKind.Primary);
        harness.Enqueue(failing, following);
        harness.RunExecutor.ExecuteAsync(failing, Arg.Any<CancellationToken>())
               .Returns(Task.FromException(new InvalidOperationException("executor terminalization failed")));

        await harness.RunToIdleAsync();

        // The failure must not take the single consumer down with it — the next claim still runs.
        await harness.RunExecutor.Received(1).ExecuteAsync(following, Arg.Any<CancellationToken>());
        AssertEx.True(harness.Logger.HasEntry(LogLevel.Error, "Benchmark queue failed while executing"),
            "The queue must report the escaped executor failure rather than dying silently.");
    }

    [Test]
    public async Task ExecuteAsync_WhenCancelledWhileExecuting_StopsWithoutClaimingAgain()
    {
        using var harness = new Harness();
        var work = Work(BenchmarkWorkKind.Primary);
        harness.Enqueue(work, Work(BenchmarkWorkKind.Primary));
        harness.RunExecutor.ExecuteAsync(work, Arg.Any<CancellationToken>())
               .Returns(_ => CancelThenThrowAsync(harness.Cancellation));

        await harness.RunToIdleAsync();

        // The cancellation guard returns instead of looping, so the second queued item is never claimed.
        AssertEx.Equal(expected: 1, harness.ClaimCount);
        AssertEx.False(harness.Logger.HasEntry(LogLevel.Error, "Benchmark queue failed while executing"),
            "A cancellation during shutdown is not an executor failure.");
    }

    private static async Task CancelThenThrowAsync(CancellationTokenSource cancellation)
    {
        await cancellation.CancelAsync();
        throw new OperationCanceledException();
    }

    private static BenchmarkClaimedWork Work(BenchmarkWorkKind kind)
    {
        var runId = Guid.NewGuid();
        return new BenchmarkClaimedWork(QueueSequence: 1, runId, kind, Attempt: 1, Version: 1, Run(runId));
    }

    private static BenchmarkRunRecord Run(Guid runId) =>
        new(runId,
            Guid.NewGuid(),
            ReadOnlyMemory<byte>.Empty,
            "model",
            PrimaryModelOrigin: null,
            "v1:fingerprint",
            "agent",
            AgentVersion: 1,
            RequestedContextTokens: 4096,
            BenchmarkPrimaryStatus.Queued,
            EffectiveContextTokens: null,
            DurationMs: null,
            TotalTokens: null,
            TokensPerSecond: null,
            OutputPartsJson: null,
            LastStreamSequence: 0,
            UserScore: null,
            BenchmarkJudgeStatus.Disabled,
            JudgeResultJson: null,
            PrimaryErrorMessage: null,
            JudgeErrorMessage: null,
            Version: 1,
            CreatedAtUtc: 1,
            StartedAtUtc: null,
            PrimaryCompletedAtUtc: null,
            JudgeStartedAtUtc: null,
            JudgeCompletedAtUtc: null,
            UpdatedAtUtc: 1);

    private sealed class Harness : IDisposable
    {
        private readonly Queue<BenchmarkClaimedWork> _queued = new();
        private readonly ServiceProvider _provider;

        public Harness()
        {
            Store.RecoverRunsOnStartupAsync(Arg.Any<CancellationToken>())
                 .Returns(_ => Task.FromResult<IReadOnlyList<BenchmarkRunRecord>>(Recovered));
            Store.ClaimNextAsync(Arg.Any<CancellationToken>())
                 .Returns(_ =>
                 {
                     ClaimCount++;
                     return Task.FromResult(_queued.Count > 0 ? _queued.Dequeue() : null);
                 });

            var services = new ServiceCollection();
            _ = services.AddSingleton(Store);
            _ = services.AddSingleton(RunExecutor);
            _ = services.AddSingleton(JudgeExecutor);
            _provider = services.BuildServiceProvider();
        }

        public IBenchmarkStore Store { get; } = Substitute.For<IBenchmarkStore>();
        public IBenchmarkEventBuffer Events { get; } = Substitute.For<IBenchmarkEventBuffer>();
        public IBenchmarkRunExecutor RunExecutor { get; } = Substitute.For<IBenchmarkRunExecutor>();
        public IBenchmarkJudgeExecutor JudgeExecutor { get; } = Substitute.For<IBenchmarkJudgeExecutor>();
        public RecordingLogger<BenchmarkQueueHostedService> Logger { get; } = new();
        public StopOnWaitSignal Signal { get; } = new();
        public CancellationTokenSource Cancellation { get; } = new();
        public IReadOnlyList<BenchmarkRunRecord> Recovered { get; set; } = [];
        public int ClaimCount { get; private set; }

        public void Enqueue(params BenchmarkClaimedWork[] work)
        {
            foreach (var item in work)
            {
                _queued.Enqueue(item);
            }
        }

        public BenchmarkQueueHostedService CreateService(TimeSpan? pollInterval = null) =>
            new(_provider.GetRequiredService<IServiceScopeFactory>(),
                Signal,
                Events,
                Options.Create(new BenchmarkQueueOptions
                {
                    PollInterval = pollInterval ?? TimeSpan.FromMilliseconds(10)
                }),
                Logger);

        public void Dispose()
        {
            Cancellation.Dispose();
            _provider.Dispose();
        }

        /// <summary>Drains everything queued and returns once the consumer has parked on an empty queue.</summary>
        public async Task RunToIdleAsync()
        {
            Signal.Cancellation = Cancellation;
            using var service = CreateService();
            await BackgroundServiceTestHelper.RunExecuteAsync(service, Cancellation.Token);
        }
    }

    /// <summary>
    ///     Stands in for <see cref="BenchmarkQueueSignal" />: the first park on an empty queue cancels the loop's token
    ///     and returns, so <c>ExecuteAsync</c> falls out of its <c>while</c> instead of waiting on wall-clock time.
    /// </summary>
    private sealed class StopOnWaitSignal : IBenchmarkQueueSignal
    {
        public int WaitCount { get; private set; }

        public CancellationTokenSource? Cancellation { get; set; }

        public void Wake()
        {
        }

        public async Task WaitAsync(TimeSpan pollInterval, CancellationToken cancellationToken)
        {
            WaitCount++;
            if (Cancellation is { } cancellation)
            {
                await cancellation.CancelAsync();
            }
        }
    }
}

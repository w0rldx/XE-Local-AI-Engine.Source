namespace XE_Local_AI_Engine.Tests.BackgroundServices;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Client.Services.Training;
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

    /// <summary>
    ///     A kind this build has no executor for is failed CLOSED, not left claimed. Leaving it Running would stall
    ///     the single consumer behind an item nothing will ever finish; succeeding it would publish a measurement
    ///     nothing took. Every kind this build declares now has an arm, so the only way here is an ordinal written by
    ///     a NEWER build — which is exactly the state the guard exists for.
    /// </summary>
    [Test]
    public async Task ExecuteAsync_WhenAKindHasNoExecutor_TerminalizesItFailedAndKeepsConsuming()
    {
        using var harness = new Harness();
        var fromANewerBuild = Work((BenchmarkWorkKind)999);
        var following = Work(BenchmarkWorkKind.Primary);
        harness.Enqueue(fromANewerBuild, following);

        await harness.RunToIdleAsync();

        _ = await harness.Store.Received(1)
                         .MarkFidelityFailedAsync(fromANewerBuild.RunId,
                             fromANewerBuild.Version,
                             Arg.Is<string>(reason => reason.Contains("not supported by this build", StringComparison.Ordinal)),
                             Arg.Any<CancellationToken>());
        await harness.RunExecutor.Received(1).ExecuteAsync(following, Arg.Any<CancellationToken>());
        AssertEx.True(harness.Logger.HasEntry(LogLevel.Error, "unsupported"),
            "An unsupported kind must be reported, not swallowed.");
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

    /// <summary>
    ///     Startup recovery runs BEFORE the first claim and outside the loop's guard, so a database failure there was
    ///     the same StopHost kill by another door. It costs neither the host nor the work items: the loop survives AND
    ///     claims nothing until recovery has succeeded once, because recovery is the only thing that terminalizes the
    ///     rows the previous process left Running — claiming past it orphans them for this process's lifetime.
    /// </summary>
    [Test]
    public async Task ExecuteAsync_WhenStartupRecoveryThrows_ClaimsNothingUntilARetrySucceeds()
    {
        using var harness = new Harness();
        var work = Work(BenchmarkWorkKind.Primary);
        harness.Enqueue(work);
        var attempts = 0;
        _ = harness.Store.RecoverRunsOnStartupAsync(Arg.Any<CancellationToken>())
                   .Returns(_ => ++attempts == 1
                       ? Task.FromException<IReadOnlyList<BenchmarkRunRecord>>(new InvalidOperationException("recovery failed"))
                       : Task.FromResult<IReadOnlyList<BenchmarkRunRecord>>([]));
        // The failed recovery parks on the poll interval; the loop must survive that park and recover again.
        harness.Signal.CancelOnWaitNumber = 2;

        await harness.RunToIdleAsync();

        AssertEx.Equal(expected: 2, attempts, "A failed recovery is retried on the poll interval rather than abandoned.");
        await harness.RunExecutor.Received(1).ExecuteAsync(work, Arg.Any<CancellationToken>());
        AssertEx.True(harness.Logger.HasEntry(LogLevel.Error, "startup recovery failed"),
            "A failed recovery must be reported, not swallowed.");
    }

    /// <summary>The half the guard alone never gave: nothing is claimed while recovery is still failing.</summary>
    [Test]
    public async Task ExecuteAsync_WhileStartupRecoveryKeepsFailing_NeverClaims()
    {
        using var harness = new Harness();
        harness.Enqueue(Work(BenchmarkWorkKind.Primary));
        _ = harness.Store.RecoverRunsOnStartupAsync(Arg.Any<CancellationToken>())
                   .Returns(_ => Task.FromException<IReadOnlyList<BenchmarkRunRecord>>(new InvalidOperationException("recovery failed")));
        harness.Signal.CancelOnWaitNumber = 3;

        await harness.RunToIdleAsync();

        AssertEx.Equal(expected: 0, harness.ClaimCount,
            "Rows the previous process left Running stay orphaned for this process's lifetime if the queue claims past a failed recovery.");
        await harness.RunExecutor.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default);
    }

    /// <summary>
    ///     The CLAIM is the call that failed in the live incident (a transient SQLite error). It sits outside the
    ///     executor's own guard, so an escaped exception ended <c>ExecuteAsync</c> and — the default
    ///     <c>BackgroundServiceExceptionBehavior</c> being <c>StopHost</c> — took the whole node down with it.
    /// </summary>
    [Test]
    public async Task ExecuteAsync_WhenClaimingThrows_LogsAndKeepsConsumingLaterWork()
    {
        using var harness = new Harness();
        var following = Work(BenchmarkWorkKind.Primary);
        harness.Enqueue(following);
        harness.FirstClaimBehavior = () => Task.FromException<BenchmarkClaimedWork?>(new InvalidOperationException("claim failed"));
        // The failed claim parks on the poll interval; the loop must survive that park and claim again.
        harness.Signal.CancelOnWaitNumber = 2;

        await harness.RunToIdleAsync();

        AssertEx.True(harness.ClaimCount >= 2, "A failed claim must be retried after the poll interval, not end the loop.");
        await harness.RunExecutor.Received(1).ExecuteAsync(following, Arg.Any<CancellationToken>());
        AssertEx.True(harness.Logger.HasEntry(LogLevel.Error, "failed while claiming work"),
            "The queue must report the failed claim rather than dying silently.");
    }

    [Test]
    public async Task ExecuteAsync_WhenClaimingThrowsDuringShutdown_StopsWithoutLoggingAFailure()
    {
        using var harness = new Harness();
        harness.FirstClaimBehavior = () => CancelThenThrowAsync<BenchmarkClaimedWork?>(harness.Cancellation);

        await harness.RunToIdleAsync();

        AssertEx.Equal(expected: 1, harness.ClaimCount);
        AssertEx.False(harness.Logger.HasEntry(LogLevel.Error, "failed while claiming work"),
            "A cancellation during shutdown is not a claim failure.");
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

    private static async Task<T> CancelThenThrowAsync<T>(CancellationTokenSource cancellation)
    {
        await cancellation.CancelAsync();
        throw new OperationCanceledException();
    }

    /// <summary>
    ///     Startup reconciliation is resolved optionally. A host that composed the queue without a pairwise planner
    ///     cannot hold pairwise work, and throwing for it would kill the consumer before its first claim — starving
    ///     every OTHER kind of benchmark work over a leg that had nothing to do.
    /// </summary>
    [Test]
    public async Task ExecuteAsync_WhenNoPairwisePlannerIsRegistered_StillRecoversAndKeepsConsuming()
    {
        using var harness = new Harness();
        var work = Work(BenchmarkWorkKind.Primary);
        harness.Enqueue(work);

        await harness.RunToIdleAsync();

        _ = await harness.Store.Received(1).RecoverRunsOnStartupAsync(Arg.Any<CancellationToken>());
        await harness.RunExecutor.Received(1).ExecuteAsync(work, Arg.Any<CancellationToken>());
        AssertEx.True(harness.Logger.HasEntry(LogLevel.Warning, "pairwise reconciliation"),
            "A skipped reconciliation must be reported, not silently dropped.");
    }

    [Test]
    public async Task ExecuteAsync_WhenAPairwisePlannerIsRegistered_ReconcilesOnStartup()
    {
        var planner = Substitute.For<IBenchmarkPairwisePlanner>();
        using var harness = new Harness(planner);

        await harness.RunToIdleAsync();

        await planner.Received(1).ReconcilePairwiseAsync(Arg.Any<CancellationToken>());
        AssertEx.False(harness.Logger.HasEntry(LogLevel.Warning, "pairwise reconciliation"),
            "A registered planner must not report a skip.");
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
            PrimaryErrorMessage: null,
            Version: 1,
            CreatedAtUtc: 1,
            StartedAtUtc: null,
            PrimaryCompletedAtUtc: null,
            UpdatedAtUtc: 1);

    private sealed class Harness : IDisposable
    {
        private readonly Queue<BenchmarkClaimedWork> _queued = new();
        private readonly ServiceProvider _provider;

        public Harness(IBenchmarkPairwisePlanner? pairwisePlanner = null)
        {
            Store.RecoverRunsOnStartupAsync(Arg.Any<CancellationToken>())
                 .Returns(_ => Task.FromResult<IReadOnlyList<BenchmarkRunRecord>>(Recovered));
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
            _ = services.AddSingleton(RunExecutor);
            _ = services.AddSingleton(JudgeExecutor);
            if (pairwisePlanner is not null)
            {
                _ = services.AddSingleton(pairwisePlanner);
            }

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

        /// <summary>Scripts the FIRST claim only; every later claim falls back to the queue.</summary>
        public Func<Task<BenchmarkClaimedWork?>>? FirstClaimBehavior { get; set; }

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
                new GpuWorkGate(),
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

        /// <summary>Which park stops the loop. Raised when a test needs the consumer to survive an idle poll first.</summary>
        public int CancelOnWaitNumber { get; set; } = 1;

        public void Wake()
        {
        }

        public async Task WaitAsync(TimeSpan pollInterval, CancellationToken cancellationToken)
        {
            WaitCount++;
            if (Cancellation is { } cancellation && WaitCount >= CancelOnWaitNumber)
            {
                await cancellation.CancelAsync();
            }
        }
    }
}

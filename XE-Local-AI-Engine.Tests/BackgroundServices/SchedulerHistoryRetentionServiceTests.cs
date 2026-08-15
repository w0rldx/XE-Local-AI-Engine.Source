namespace XE_Local_AI_Engine.Tests.BackgroundServices;

using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.BackgroundServices;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Scheduler;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Unit tests for the scheduler-history retention sweeper. Its cadence is expressed in whole minutes, so the loop is
///     driven by <see cref="ManualTimeProvider" /> rather than by wall-clock waiting; the assertions cover the three
///     things that can silently go wrong — sweeping while the scheduler is disabled, computing the cutoff in the wrong
///     unit (run rows stamp <c>CreatedAtUtc</c> in unix <b>milliseconds</b>), and a failed sweep killing the loop.
/// </summary>
public sealed class SchedulerHistoryRetentionServiceTests
{
    private const int RetentionDays = 30;

    [Test]
    public async Task ExecuteAsync_WhenTheSchedulerIsDisabled_ReturnsWithoutEverSweeping()
    {
        using var harness = new Harness(enabled: false);
        using var service = harness.CreateService();

        // No cancellation is needed: the disabled path must return on its own rather than parking on a timer.
        await BackgroundServiceTestHelper.RunExecuteAsync(service, CancellationToken.None);

        AssertEx.Empty(harness.Cutoffs);
    }

    [Test]
    public async Task ExecuteAsync_OnEachTick_SweepsWithARetentionCutoffInUnixMilliseconds()
    {
        using var harness = new Harness(enabled: true);
        using var service = harness.CreateService();

        await RunLoopAsync(service, harness, async () =>
        {
            harness.Time.Advance(TimeSpan.FromMinutes(1));

            await AssertEx.EventuallyAsync(() => !harness.Cutoffs.IsEmpty, TimeSpan.FromSeconds(5));
            var expected = harness.Time.GetUtcNow().AddDays(-RetentionDays).ToUnixTimeMilliseconds();
            AssertEx.True(harness.Cutoffs.TryPeek(out var actualCutoff));
            AssertEx.Equal(expected, actualCutoff);
        });
    }

    [Test]
    public async Task ExecuteAsync_WhenASweepFails_LogsAndKeepsSweepingOnTheNextTick()
    {
        using var harness = new Harness(enabled: true, failFirstSweep: true);
        using var service = harness.CreateService();

        await RunLoopAsync(service, harness, async () =>
        {
            harness.Time.Advance(TimeSpan.FromMinutes(1));
            await AssertEx.EventuallyAsync(() => harness.Logger.HasEntry(LogLevel.Warning, "Scheduler history retention sweep failed."),
                TimeSpan.FromSeconds(5));
            harness.Time.Advance(TimeSpan.FromMinutes(1));

            await AssertEx.EventuallyAsync(() => harness.Cutoffs.Count >= 2,
                TimeSpan.FromSeconds(5),
                "A failed sweep must not take the retention loop down with it.");
        });
    }

    [Test]
    public async Task ExecuteAsync_WhenCancelled_StopsCleanlyWithoutSweeping()
    {
        using var harness = new Harness(enabled: true);
        using var service = harness.CreateService();

        // The loop absorbs the shutdown token rather than faulting the host with an escaped cancellation.
        await RunLoopAsync(service, harness, static () => Task.CompletedTask);

        AssertEx.Empty(harness.Cutoffs);
    }

    /// <summary>
    ///     Runs the sweeper loop for the duration of <paramref name="body" /> and always drains it before the caller's
    ///     <c>using</c> scope disposes the service or the token source, including when an assertion inside the body
    ///     throws.
    /// </summary>
    private static async Task RunLoopAsync(SchedulerHistoryRetentionService service, Harness harness, Func<Task> body)
    {
        var loop = BackgroundServiceTestHelper.RunExecuteAsync(service, harness.Cancellation.Token);
        try
        {
            await body();
        }
        finally
        {
            await harness.Cancellation.CancelAsync();
            await loop;
        }
    }

    private sealed class Harness : IDisposable
    {
        private readonly SchedulerOptions _options;
        private readonly ServiceProvider _provider;
        private readonly bool _failFirstSweep;

        public Harness(bool enabled, bool failFirstSweep = false)
        {
            _failFirstSweep = failFirstSweep;
            _options = new SchedulerOptions
            {
                Enabled = enabled,
                HistoryRetentionDays = RetentionDays,
                RetentionSweepIntervalMinutes = 1
            };

            _ = RunStore.SweepOlderThanAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(RecordSweep);

            var services = new ServiceCollection();
            _ = services.AddSingleton(RunStore);
            _provider = services.BuildServiceProvider();
        }

        public IScheduledJobRunStore RunStore { get; } = Substitute.For<IScheduledJobRunStore>();
        public ManualTimeProvider Time { get; } = new();
        public RecordingLogger<SchedulerHistoryRetentionService> Logger { get; } = new();
        public CancellationTokenSource Cancellation { get; } = new();
        public ConcurrentQueue<long> Cutoffs { get; } = new();

        public SchedulerHistoryRetentionService CreateService() =>
            new(_provider.GetRequiredService<IServiceScopeFactory>(), Time, Options.Create(_options), Logger);

        public void Dispose()
        {
            Cancellation.Dispose();
            _provider.Dispose();
        }

        private int RecordSweep(NSubstitute.Core.CallInfo call)
        {
            Cutoffs.Enqueue(call.Arg<long>());
            if (_failFirstSweep && Cutoffs.Count == 1)
            {
                throw new InvalidOperationException("scheduler history table is locked");
            }

            return 0;
        }
    }
}

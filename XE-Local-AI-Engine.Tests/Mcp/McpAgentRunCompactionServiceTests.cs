namespace XE_Local_AI_Engine.Tests.Mcp;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Mcp.Runs;
using XE_Local_AI_Engine.Tests.Testing;

[NotInParallel]
public sealed class McpAgentRunCompactionServiceTests
{
    [Test]
    public async Task ExecuteAsync_AfterCommittedCompaction_RefreshesCurrentStateGauges()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var store = Substitute.For<IMcpAgentRunStore>();
        store.CompactExpiredPayloadsAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(1);
        store.GetLedgerSnapshotAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            stop.Cancel();
            return new McpAgentRunLedgerSnapshot(QueueDepth: 0,
                RunningCount: 0,
                new McpAgentRunLedgerCounters(AccountingVersion: 1,
                    NonterminalRunCount: 0,
                    QueuedRunCount: 0,
                    RunningRunCount: 0,
                    IdentityCount: 1,
                    ActivePayloadBytes: 0,
                    TombstoneLogicalBytes: 288,
                    UpdatedAtUtc: 1));
        });
        var services = new ServiceCollection();
        services.AddSingleton(store);
        await using var provider = services.BuildServiceProvider();
        using var metrics = new McpAgentRunMetrics();
        using var service = new McpAgentRunCompactionService(provider.GetRequiredService<IServiceScopeFactory>(),
            metrics,
            Options.Create(new McpAgentRunOptions()),
            TimeProvider.System,
            NullLogger<McpAgentRunCompactionService>.Instance);

        await BackgroundServiceTestHelper.RunExecuteAsync(service, stop.Token).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        await store.Received(1).CompactExpiredPayloadsAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
        await store.Received(1).GetLedgerSnapshotAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecuteAsync_AfterHandledFailure_WaitsForNextIntervalBeforeRetrying()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var firstAttempt = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondAttempt = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var attemptCount = 0;
        var store = Substitute.For<IMcpAgentRunStore>();
        store.CompactExpiredPayloadsAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns<Task<int>>(_ =>
        {
            var attempt = Interlocked.Increment(ref attemptCount);
            (attempt == 1 ? firstAttempt : secondAttempt).TrySetResult(result: true);
            return Task.FromException<int>(new IOException("simulated compaction failure"));
        });
        var services = new ServiceCollection();
        services.AddSingleton(store);
        await using var provider = services.BuildServiceProvider();
        using var metrics = new McpAgentRunMetrics();
        using var time = new ManualTimerTimeProvider();
        var service = new McpAgentRunCompactionService(provider.GetRequiredService<IServiceScopeFactory>(),
            metrics,
            Options.Create(new McpAgentRunOptions { CompactionIntervalMinutes = 1 }),
            time,
            NullLogger<McpAgentRunCompactionService>.Instance);
        try
        {
            var execution = BackgroundServiceTestHelper.RunExecuteAsync(service, stop.Token);
            await firstAttempt.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            await Task.Yield();
            AssertEx.Equal(expected: 1, Volatile.Read(ref attemptCount));

            time.FireTimer();
            await secondAttempt.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            await stop.CancelAsync().ConfigureAwait(false);
            await execution.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

            AssertEx.Equal(expected: 2, Volatile.Read(ref attemptCount));
        }
        finally
        {
            service.Dispose();
        }
    }

    private sealed class ManualTimerTimeProvider : TimeProvider, IDisposable
    {
        private ManualTimer? _timer;

        public override ITimer CreateTimer(TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            _timer = new ManualTimer(callback, state);
            return _timer;
        }

        public void FireTimer()
        {
            (_timer ?? throw new InvalidOperationException("The compaction timer was not created.")).Fire();
        }

        public void Dispose()
        {
            _timer?.Dispose();
            _timer = null;
        }

        private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            private TimerCallback? _callback = callback;

            public bool Change(TimeSpan dueTime, TimeSpan period) => _callback is not null;

            public void Dispose()
            {
                _callback = null;
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            public void Fire()
            {
                _callback?.Invoke(state);
            }
        }
    }
}

namespace XE_Local_AI_Engine.Tests.Chat;

using XE_Local_AI_Engine.Client.Services.Chat.Compaction;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The background worker behind automatic compaction: one job at a time, each in its own DI scope, a failing job
///     isolated from the next, and queued work drained at shutdown.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class ConversationMaintenanceWorkerTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Test]
    public async Task Worker_RunsEachJobInItsOwnScope()
    {
        await using var harness = new ConversationMaintenanceHarness();
        await harness.Worker.StartAsync(CancellationToken.None);

        harness.Dispatcher.Dispatch(ConversationMaintenanceHarness.Job(Guid.NewGuid()));
        harness.Dispatcher.Dispatch(ConversationMaintenanceHarness.Job(Guid.NewGuid()));
        await AssertEx.EventuallyAsync(() => harness.Compactions.Count == 2, Wait, "Both queued jobs should compact.");
        await harness.Worker.StopAsync(CancellationToken.None);

        var instances = harness.Compactions.Select(static call => call.Instance).Distinct().Count();
        AssertEx.Equal(expected: 2, instances, "Each job resolves its scoped services from a fresh scope, never the request scope that queued it.");
    }

    [Test]
    public async Task Worker_WhenAJobThrows_KeepsRunningAndReleasesThatConversation()
    {
        await using var harness = new ConversationMaintenanceHarness();
        var failing = Guid.NewGuid();
        harness.OnCompact = (conversationId, _) => conversationId == failing && harness.Compactions.Count == 1
            ? throw new InvalidOperationException("fold blew up")
            : Task.CompletedTask;
        await harness.Worker.StartAsync(CancellationToken.None);

        harness.Dispatcher.Dispatch(ConversationMaintenanceHarness.Job(failing));
        await AssertEx.EventuallyAsync(() => harness.Logger.AllText.Contains("InvalidOperationException", StringComparison.Ordinal), Wait);

        harness.Dispatcher.Dispatch(ConversationMaintenanceHarness.Job(Guid.NewGuid()));
        harness.Dispatcher.Dispatch(ConversationMaintenanceHarness.Job(failing));
        await AssertEx.EventuallyAsync(() => harness.Compactions.Count == 3,
            Wait,
            "The worker must survive the failed job, and the failed conversation's coalescing slot must be released.");
        await harness.Worker.StopAsync(CancellationToken.None);

        AssertEx.False(harness.Logger.AllText.Contains("fold blew up", StringComparison.Ordinal), "Only the exception type is logged, never its message.");
    }

    [Test]
    public async Task StopAsync_DrainsJobsStillQueuedAtShutdown()
    {
        await using var harness = new ConversationMaintenanceHarness();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.OnCompact = async (_, _) =>
        {
            if (harness.Compactions.Count == 1)
            {
                firstEntered.TrySetResult();
                await release.Task;
            }
        };
        await harness.Worker.StartAsync(CancellationToken.None);

        for (var i = 0; i < 3; i++)
        {
            harness.Dispatcher.Dispatch(ConversationMaintenanceHarness.Job(Guid.NewGuid()));
        }

        await firstEntered.Task.WaitAsync(Wait);
        var stop = harness.Worker.StopAsync(CancellationToken.None);
        release.TrySetResult();
        await stop.WaitAsync(Wait);

        AssertEx.Equal(expected: 3, harness.Compactions.Count, "Jobs queued behind the running one run before shutdown completes.");
    }

    [Test]
    public async Task StopAsync_WhenTheDrainWindowElapses_CancelsTheRunningJobAndDropsTheRest()
    {
        var clock = new ManualTimeProvider();
        await using var harness = new ConversationMaintenanceHarness(new ConversationCompactionOptions
            {
                MaintenanceShutdownDrainTimeoutSeconds = 1
            },
            timeProvider: clock);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.OnCompact = async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        await harness.Worker.StartAsync(CancellationToken.None);

        harness.Dispatcher.Dispatch(ConversationMaintenanceHarness.Job(Guid.NewGuid()));
        harness.Dispatcher.Dispatch(ConversationMaintenanceHarness.Job(Guid.NewGuid()));
        await entered.Task.WaitAsync(Wait);

        // StopAsync arms its drain timer synchronously, because the parked job keeps the read loop pending.
        var stop = harness.Worker.StopAsync(CancellationToken.None);
        AssertEx.False(stop.IsCompleted, "Shutdown waits for the running job until the drain window elapses.");
        AssertEx.Equal(expected: 1, clock.ArmedTimerCount, "The drain window is armed before the clock moves.");
        clock.Advance(TimeSpan.FromSeconds(1));
        await stop.WaitAsync(Wait);

        AssertEx.Contains(harness.Logger.AllText, "abandoned 0 running and dropped 1 queued job(s)");
        AssertEx.Contains(harness.Logger.AllText, "interrupted by shutdown");
        AssertEx.Equal(expected: 1, harness.Compactions.Count);
    }
}

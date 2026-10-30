namespace XE_Local_AI_Engine.Tests.Events;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Events.Implementation;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;

/// <summary>
///     Background model work over the real <see cref="WorkerEventDispatcher" /> slot and a manual clock: it starts only
///     after the slot stayed free for the idle window, and a turn taking the slot mid-call cancels it and reruns it later.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class BackgroundInvocationYieldTests
{
    [Test]
    public async Task Work_WhileATurnHoldsTheSlot_WaitsForTheReleaseAndThenTheIdleWindow()
    {
        var harness = new Harness();
        var lease = await harness.TakeSlotAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var run = harness.Yield.RunWhenIdleAsync("test", _ => Start(started), CancellationToken.None);

        await AssertEx.StaysIncompleteAsync(started.Task, "no background work while a turn holds the slot");
        AssertEx.Equal(expected: 0, harness.Time.ArmedTimerCount, "the idle window starts only once the slot is free");

        await lease.DisposeAsync();
        await harness.WindowArmedAsync();
        await AssertEx.StaysIncompleteAsync(started.Task, "the slot is free, but the idle window has not elapsed");

        harness.Time.Advance(BackgroundInvocationYield.IdleWindow);
        await AssertEx.CompletesAsync(run, TestBudgets.Contended, "the work runs once the slot stayed free for the window");
    }

    [Test]
    public async Task Work_OnAFreeSlot_StartsOnlyWhenTheIdleWindowElapses()
    {
        var harness = new Harness();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var run = harness.Yield.RunWhenIdleAsync("test", _ => Start(started), CancellationToken.None);
        await harness.WindowArmedAsync();

        harness.Time.Advance(BackgroundInvocationYield.IdleWindow - TimeSpan.FromMilliseconds(1));
        await AssertEx.StaysIncompleteAsync(started.Task, "a millisecond short of the window is not idle yet");

        harness.Time.Advance(TimeSpan.FromMilliseconds(1));
        await AssertEx.CompletesAsync(run, TestBudgets.Contended, "the work runs when the window elapses");
    }

    [Test]
    public async Task ATurnInsideTheIdleWindow_RestartsTheWindowFromItsRelease()
    {
        var harness = new Harness();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var run = harness.Yield.RunWhenIdleAsync("test", _ => Start(started), CancellationToken.None);
        await harness.WindowArmedAsync();
        harness.Time.Advance(TimeSpan.FromSeconds(1));

        var lease = await harness.TakeSlotAsync();
        await AssertEx.EventuallyAsync(() => harness.Time.ArmedTimerCount == 0, TestBudgets.Contended, "the take should drop the running window");
        await lease.DisposeAsync();
        await harness.WindowArmedAsync();

        harness.Time.Advance(TimeSpan.FromSeconds(1.5));
        await AssertEx.StaysIncompleteAsync(started.Task, "the window counts from the release, not from the first free moment");

        harness.Time.Advance(TimeSpan.FromSeconds(0.5));
        await AssertEx.CompletesAsync(run, TestBudgets.Contended, "a full window after the release, the work runs");
    }

    [Test]
    public async Task ATurnTakingTheSlotMidCall_CancelsTheCall_AndTheJobRerunsToCompletionAtTheNextIdleWindow()
    {
        var harness = new Harness();
        var job = new ParkingJob();

        var run = harness.Yield.RunWhenIdleAsync("test", job.RunAsync, CancellationToken.None);
        await harness.PassIdleWindowAsync();
        await AssertEx.CompletesAsync(job.FirstStarted.Task, TestBudgets.Contended, "the first attempt starts on the idle slot");

        var lease = await harness.TakeSlotAsync();
        await AssertEx.CompletesAsync(job.FirstCancelled.Task, TestBudgets.Contended, "taking the slot cancels the in-flight call");
        await AssertEx.StaysIncompleteAsync(run, "a yielded job is requeued, not finished and not dropped");

        await lease.DisposeAsync();
        await harness.PassIdleWindowAsync();
        await AssertEx.CompletesAsync(run, TestBudgets.Contended, "the requeued job runs at the next idle window");

        AssertEx.Equal(expected: 2, job.Attempts, "one cancelled attempt, one completed rerun");
        AssertEx.Equal(expected: 1, job.Completions, "the job completes exactly once");
        AssertEx.True(harness.Logger.HasEntry(LogLevel.Information, "yielded"), "the yield is logged as information");
        AssertEx.False(harness.Logger.Entries.Any(static entry => entry.Level >= LogLevel.Warning), "a yield is not a failure");
    }

    [Test]
    public async Task Shutdown_WhileWaitingForTheSlot_ThrowsCancellation_AndNeverRunsTheWork()
    {
        var harness = new Harness();
        await using var lease = await harness.TakeSlotAsync();
        using var shutdown = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var run = harness.Yield.RunWhenIdleAsync("test", _ => Start(started), shutdown.Token);
        await AssertEx.StaysIncompleteAsync(run, "waiting on the held slot");
        await shutdown.CancelAsync();

        _ = await AssertEx.ThrowsAsync<OperationCanceledException>(() => run, "shutdown ends the wait");
        AssertEx.False(started.Task.IsCompleted, "the work never ran");
    }

    [Test]
    public async Task Shutdown_DuringTheCall_ThrowsCancellation_WithoutARerun()
    {
        var harness = new Harness();
        var job = new ParkingJob();
        using var shutdown = new CancellationTokenSource();

        var run = harness.Yield.RunWhenIdleAsync("test", job.RunAsync, shutdown.Token);
        await harness.PassIdleWindowAsync();
        await AssertEx.CompletesAsync(job.FirstStarted.Task, TestBudgets.Contended, "the call is in flight");
        await shutdown.CancelAsync();

        _ = await AssertEx.ThrowsAsync<OperationCanceledException>(() => run, "shutdown cancels the call and ends the run");
        AssertEx.Equal(expected: 1, job.Attempts, "a shutdown cancel is not a yield, so nothing reruns");
        AssertEx.False(harness.Logger.HasEntry(LogLevel.Information, "yielded"), "a shutdown is not logged as a yield");
    }

    [Test]
    public async Task AFailingCall_OnAnIdleSlot_PropagatesWithoutARerun()
    {
        var harness = new Harness();
        var attempts = 0;

        var run = harness.Yield.RunWhenIdleAsync("test",
            token =>
            {
                _ = Interlocked.Increment(ref attempts);
                throw new InvalidOperationException("model failed");
            },
            CancellationToken.None);
        await harness.PassIdleWindowAsync();

        _ = await AssertEx.ThrowsAsync<InvalidOperationException>(() => run, "a real failure is the caller's to record");
        AssertEx.Equal(expected: 1, attempts, "only a yield reruns the work");
    }

    private static Task Start(TaskCompletionSource started)
    {
        started.TrySetResult();
        return Task.CompletedTask;
    }

    /// <summary>The first attempt parks until cancelled; every later attempt completes at once.</summary>
    private sealed class ParkingJob
    {
        private int _attempts;
        private int _completions;

        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FirstCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Attempts => Volatile.Read(ref _attempts);

        public int Completions => Volatile.Read(ref _completions);

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _attempts) == 1)
            {
                FirstStarted.TrySetResult();
                try
                {
                    await new TaskCompletionSource().Task.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    FirstCancelled.TrySetResult();
                    throw;
                }
            }

            _ = Interlocked.Increment(ref _completions);
        }
    }

    private sealed class Harness
    {
        public Harness()
        {
            Yield = new BackgroundInvocationYield(Dispatcher, Time, Logger);
        }

        public ManualTimeProvider Time { get; } = new();

        public RecordingLogger<BackgroundInvocationYield> Logger { get; } = new();

        public WorkerEventDispatcher Dispatcher { get; } = new(Substitute.For<IInvocationRunner>(),
            Substitute.For<IInvocationHistory>(),
            NullLogger<WorkerEventDispatcher>.Instance,
            TimeProvider.System);

        public BackgroundInvocationYield Yield { get; }

        public Task<IAsyncDisposable> TakeSlotAsync() =>
            Dispatcher.ReportInvocationAssignedAsync(RuntimePackageBuilder.Valid().WithInvocationId(Guid.NewGuid()).WithConversationId(Guid.NewGuid()).Build());

        public Task WindowArmedAsync() =>
            AssertEx.EventuallyAsync(() => Time.ArmedTimerCount == 1, TestBudgets.Contended, "the idle window timer should be armed");

        public async Task PassIdleWindowAsync()
        {
            await WindowArmedAsync();
            Time.Advance(BackgroundInvocationYield.IdleWindow);
        }
    }
}

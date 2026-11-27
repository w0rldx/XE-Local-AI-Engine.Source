namespace XE_Local_AI_Engine.Tests.Testing;

/// <summary>Covers <see cref="ImmediateTimerTimeProvider" />: a finite timer moves the clock and fires, an infinite one does neither.</summary>
[Category(TestCategories.Unit)]
public sealed class ImmediateTimerTimeProviderTests
{
    [Test]
    public async Task CreateTimer_WithAFiniteDueTime_MovesTheClockByItAndFires()
    {
        var time = new ImmediateTimerTimeProvider(DateTimeOffset.UnixEpoch);
        var fired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var timer = time.CreateTimer(_ => fired.TrySetResult(), state: null, TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);

        AssertEx.Equal(DateTimeOffset.UnixEpoch.AddSeconds(5), time.GetUtcNow());
        await fired.Task.WaitAsync(TestBudgets.Contended);
    }

    [Test]
    public void CreateTimer_WithAnInfiniteDueTime_LeavesTheClockWhereItIs()
    {
        var time = new ImmediateTimerTimeProvider(DateTimeOffset.UnixEpoch);

        using var timer = time.CreateTimer(_ => throw new InvalidOperationException("An infinite due time never fires."), state: null,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        AssertEx.Equal(DateTimeOffset.UnixEpoch, time.GetUtcNow());
    }
}

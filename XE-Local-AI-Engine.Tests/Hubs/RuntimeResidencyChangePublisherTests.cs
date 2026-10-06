namespace XE_Local_AI_Engine.Tests.Hubs;

using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Hubs;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="RuntimeResidencyChangePublisher" />: trailing-edge coalescing plus a minimum spacing between ticks on the
///     injected clock, a send failure that never reaches the caller, and a stop that leaves no armed timer behind.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class RuntimeResidencyChangePublisherTests
{
    [Test]
    public async Task Burst_CoalescesIntoOneTrailingTick()
    {
        var (hubContext, sent) = ProviderStatusHubWireTests.CapturingHubContext<RuntimeResidencyHub>();
        var clock = new ManualTimeProvider();
        await using var publisher = new RuntimeResidencyChangePublisher(hubContext, clock, NullLogger<RuntimeResidencyChangePublisher>.Instance);

        for (var i = 0; i < 100; i++)
        {
            publisher.NotifyChanged();
        }

        AssertEx.Equal(expected: 1, clock.ArmedTimerCount, "A burst arms one timer, not one per change.");
        clock.Advance(RuntimeResidencyChangePublisher.CoalescingWindow - TimeSpan.FromMilliseconds(1));
        AssertEx.Equal(expected: 0, sent.Count, "Trailing edge: nothing is sent before the window closes.");

        clock.Advance(TimeSpan.FromMilliseconds(1));
        await AssertEx.EventuallyAsync(() => sent.Count == 1, TestBudgets.Contended);
        AssertEx.Equal(expected: 0, clock.ArmedTimerCount, "Nothing is pending after the tick.");
    }

    [Test]
    public async Task SustainedChurn_SendsOneTickPerMinSpacing_WithMonotonicSequence()
    {
        // Request churn (ensure, lease, release on every call): a change every 10 ms for 20 s.
        var (hubContext, sent) = ProviderStatusHubWireTests.CapturingHubContext<RuntimeResidencyHub>();
        var clock = new ManualTimeProvider();
        await using var publisher = new RuntimeResidencyChangePublisher(hubContext, clock, NullLogger<RuntimeResidencyChangePublisher>.Instance);
        var step = TimeSpan.FromMilliseconds(10);
        var steps = (int)(TimeSpan.FromSeconds(20) / step);

        for (var i = 0; i < steps; i++)
        {
            publisher.NotifyChanged();
            clock.Advance(step);
        }

        // Ticks at 0.5, 4.5, 8.5, 12.5 and 16.5 s; the change after the last one is armed for 20.5 s, still pending.
        const int expected = 5;
        await AssertEx.EventuallyAsync(() => sent.Count == expected, TestBudgets.Contended, $"Expected exactly {expected} ticks in 20 s, saw {sent.Count}.");
        AssertEx.Equal(expected: 1, clock.ArmedTimerCount);
        var sequences = string.Join(',', sent.Select(static message => ((RuntimeResidencyChangedHubMessage)message.Arguments[0]!).Sequence));
        AssertEx.Equal(string.Join(',', Enumerable.Range(start: 1, expected)), sequences, "Sequences must run 1..N in send order.");
    }

    [Test]
    public async Task ChangeSoonAfterATick_WaitsForTheMinSpacing()
    {
        var (hubContext, sent) = ProviderStatusHubWireTests.CapturingHubContext<RuntimeResidencyHub>();
        var clock = new ManualTimeProvider();
        await using var publisher = new RuntimeResidencyChangePublisher(hubContext, clock, NullLogger<RuntimeResidencyChangePublisher>.Instance);
        publisher.NotifyChanged();
        clock.Advance(RuntimeResidencyChangePublisher.CoalescingWindow);
        await AssertEx.EventuallyAsync(() => sent.Count == 1, TestBudgets.Contended);

        clock.Advance(TimeSpan.FromSeconds(1));
        publisher.NotifyChanged();
        clock.Advance(RuntimeResidencyChangePublisher.MinTickSpacing - TimeSpan.FromSeconds(1) - TimeSpan.FromMilliseconds(1));
        AssertEx.Equal(expected: 1, sent.Count, "A change 1 s after a tick must wait until the spacing has elapsed.");

        clock.Advance(TimeSpan.FromMilliseconds(1));
        await AssertEx.EventuallyAsync(() => sent.Count == 2, TestBudgets.Contended);
        AssertEx.Equal(expected: 0, clock.ArmedTimerCount);
    }

    [Test]
    public async Task ChangeLongAfterATick_GoesOutAfterTheCoalescingWindow()
    {
        var (hubContext, sent) = ProviderStatusHubWireTests.CapturingHubContext<RuntimeResidencyHub>();
        var clock = new ManualTimeProvider();
        await using var publisher = new RuntimeResidencyChangePublisher(hubContext, clock, NullLogger<RuntimeResidencyChangePublisher>.Instance);
        publisher.NotifyChanged();
        clock.Advance(RuntimeResidencyChangePublisher.CoalescingWindow);
        await AssertEx.EventuallyAsync(() => sent.Count == 1, TestBudgets.Contended);

        clock.Advance(TimeSpan.FromSeconds(10));
        publisher.NotifyChanged();
        clock.Advance(RuntimeResidencyChangePublisher.CoalescingWindow - TimeSpan.FromMilliseconds(1));
        AssertEx.Equal(expected: 1, sent.Count);

        clock.Advance(TimeSpan.FromMilliseconds(1));
        await AssertEx.EventuallyAsync(() => sent.Count == 2, TestBudgets.Contended, "After a quiet period the tick is due one coalescing window out.");
    }

    [Test]
    public async Task SendFailure_IsSwallowed_AndTheNextTickStillGoesOut()
    {
        var calls = 0;
        var proxy = Substitute.For<IClientProxy>();
        proxy.SendCoreAsync(Arg.Any<string>(), Arg.Any<object?[]>(), Arg.Any<CancellationToken>())
             .Returns(_ => Interlocked.Increment(ref calls) == 1
                 ? Task.FromException(new InvalidOperationException("transport down"))
                 : Task.CompletedTask);
        var clients = Substitute.For<IHubClients>();
        clients.All.Returns(proxy);
        var hubContext = Substitute.For<IHubContext<RuntimeResidencyHub>>();
        hubContext.Clients.Returns(clients);
        var clock = new ManualTimeProvider();
        await using var publisher = new RuntimeResidencyChangePublisher(hubContext, clock, NullLogger<RuntimeResidencyChangePublisher>.Instance);

        publisher.NotifyChanged();
        clock.Advance(RuntimeResidencyChangePublisher.CoalescingWindow);
        await AssertEx.EventuallyAsync(() => Volatile.Read(ref calls) == 1, TestBudgets.Contended);

        publisher.NotifyChanged();
        clock.Advance(RuntimeResidencyChangePublisher.MinTickSpacing);
        await AssertEx.EventuallyAsync(() => Volatile.Read(ref calls) == 2, TestBudgets.Contended,
            "A failed send must not wedge the publisher.");
    }

    [Test]
    public async Task Dispose_DisarmsThePendingTick_AndALaterChangeIsDropped()
    {
        var (hubContext, sent) = ProviderStatusHubWireTests.CapturingHubContext<RuntimeResidencyHub>();
        var clock = new ManualTimeProvider();
        var publisher = new RuntimeResidencyChangePublisher(hubContext, clock, NullLogger<RuntimeResidencyChangePublisher>.Instance);
        publisher.NotifyChanged();
        AssertEx.Equal(expected: 1, clock.ArmedTimerCount);

        await publisher.DisposeAsync();
        publisher.NotifyChanged();

        AssertEx.Equal(expected: 0, clock.ArmedTimerCount, "Disposal leaves no timer armed, and a change during shutdown arms none.");
        clock.Advance(RuntimeResidencyChangePublisher.CoalescingWindow * 4);
        AssertEx.Equal(expected: 0, sent.Count);
        await publisher.DisposeAsync();
    }
}

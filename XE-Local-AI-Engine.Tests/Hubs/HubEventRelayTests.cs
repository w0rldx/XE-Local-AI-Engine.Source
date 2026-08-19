namespace XE_Local_AI_Engine.Tests.Hubs;

using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Hubs;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Covers the shared relay machinery behind every per-scope hub bridge: a buffered event reaches its group with the
///     feature's method name, and a saturated bound drops the transport copy with a warning instead of blocking the
///     publisher.
/// </summary>
public sealed class HubEventRelayTests
{
    [Test]
    public async Task PublishedEvent_IsSentToTheRunGroup()
    {
        var buffer = new BenchmarkEventBuffer(Options.Create(new BenchmarkEventBufferOptions()));
        var proxy = Substitute.For<IClientProxy>();
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        proxy.SendCoreAsync(Arg.Any<string>(), Arg.Any<object?[]>(), Arg.Any<CancellationToken>())
             .Returns(_ =>
             {
                 delivered.TrySetResult();
                 return Task.CompletedTask;
             });
        var clients = Substitute.For<IHubClients>();
        clients.Group(Arg.Any<string>()).Returns(proxy);
        var hubContext = Substitute.For<IHubContext<BenchmarkRunHub>>();
        hubContext.Clients.Returns(clients);
        var runId = Guid.NewGuid();

        using var relay = new BenchmarkRunHubEventRelay(buffer, hubContext, new RecordingLogger<BenchmarkRunHubEventRelay>());
        await relay.StartAsync(CancellationToken.None);
        var published = buffer.Append(runId, BenchmarkRunStreamEventKind.OutputDelta, new BenchmarkRunStreamPayload(Content: "live output"));
        await delivered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await relay.StopAsync(CancellationToken.None);

        _ = clients.Received(1).Group(BenchmarkRunHub.RunGroup(runId));
        await proxy.Received(1).SendCoreAsync(BenchmarkRunHubEvents.Event,
            Arg.Is<object?[]>(arguments => arguments.Length == 1 && ReferenceEquals(arguments[0], published)),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Enqueue_WhenTheBoundIsReached_DropsTheTransportCopyAndWarns()
    {
        var logger = new RecordingLogger<BenchmarkRunHubEventRelay>();

        // Nothing drains the channel here, so the second write hits the bound deterministically.
        using var relay = new ProbeRelay(Substitute.For<IHubContext<BenchmarkRunHub>>(), logger);
        relay.Publish("first");
        relay.Publish("second");
        await relay.StopAsync(CancellationToken.None);

        AssertEx.False(logger.HasEntry(LogLevel.Warning, "saturated for first"));
        AssertEx.True(logger.HasEntry(LogLevel.Warning, "saturated for second"));
        AssertEx.Equal(1, relay.UnsubscribeCount);
    }

    private sealed class ProbeRelay(IHubContext<BenchmarkRunHub> hubContext, ILogger logger) : HubEventRelay<string, BenchmarkRunHub>(hubContext,
        logger,
        capacity: 1,
        "probe.event",
        static value => $"probe-{value}",
        static (log, value) => log.LogWarning("The probe relay was saturated for {Value}.", value))
    {
        public int UnsubscribeCount { get; private set; }

        public void Publish(string value) =>
            Enqueue(value);

        protected override void Subscribe()
        {
        }

        protected override void Unsubscribe() =>
            UnsubscribeCount++;
    }
}

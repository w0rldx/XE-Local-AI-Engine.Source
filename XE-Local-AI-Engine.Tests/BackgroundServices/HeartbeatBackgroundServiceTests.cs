namespace XE_Local_AI_Engine.Tests.BackgroundServices;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.BackgroundServices;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Services.Capabilities;
using XE_Local_AI_Engine.Client.Services.Connection;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Mocks;

// The service is steered through two mutable statics, and Dispose resets them. Run in parallel and a sibling's teardown
// resets the delay under whoever is still looping, so the shared key serializes them — the same idiom
// AutoConnectBackgroundServiceTests uses for the identical static-override pattern.
[NotInParallel(nameof(HeartbeatBackgroundServiceTests))]
public sealed class HeartbeatBackgroundServiceTests : IDisposable
{
    // Every wait below is on a signal the loop itself raises, and the budget is deliberately far larger than the work.
    // A wall-clock budget is what made this suite flaky: when the thread pool is saturated the continuation after
    // Task.Delay can sit unscheduled for seconds, so "the loop ran for two seconds" is not the same claim as "the loop
    // completed an iteration", and the assertion read the difference as a missing heartbeat.
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(30);

    public void Dispose()
    {
        HeartbeatBackgroundService.TestDelayOverride = TimeSpan.Zero;
        HeartbeatBackgroundService.TestCapabilityRefreshIntervalOverride = TimeSpan.Zero;
    }

    [Test]
    public async Task ExecuteAsync_WhenConnectedAndPaired_SendsHeartbeat()
    {
        HeartbeatBackgroundService.TestDelayOverride = TimeSpan.FromMilliseconds(10);
        var hubConnection = CreateHubConnection(WorkerConnectionState.Connected);

        try
        {
            using var service = CreateService(hubConnection, MockTokenStore.Paired("token", Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(1)));
            var heartbeatSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            hubConnection.SendHeartbeatAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                         .Returns(Task.CompletedTask)
                         .AndDoes(_ => heartbeatSent.TrySetResult());

            await RunUntilAsync(service, heartbeatSent.Task);

            await hubConnection.Received().SendHeartbeatAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            await hubConnection.DisposeAsync();
        }
    }

    [Test]
    public async Task ExecuteAsync_WhenNotConnected_SkipsHeartbeat()
    {
        HeartbeatBackgroundService.TestDelayOverride = TimeSpan.FromMilliseconds(10);
        var loopIterated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hubConnection = CreateHubConnection(WorkerConnectionState.Disconnected, loopIterated);

        try
        {
            using var service = CreateService(hubConnection, MockTokenStore.Paired("token", Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(1)));

            await RunUntilAsync(service, loopIterated.Task);

            await hubConnection.DidNotReceive().SendHeartbeatAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            await hubConnection.DisposeAsync();
        }
    }

    [Test]
    public async Task ExecuteAsync_WhenConnectedButNoClientNodeId_SkipsHeartbeat()
    {
        HeartbeatBackgroundService.TestDelayOverride = TimeSpan.FromMilliseconds(10);
        var loopIterated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hubConnection = CreateHubConnection(WorkerConnectionState.Connected, loopIterated);

        try
        {
            using var service = CreateService(hubConnection, MockTokenStore.Unpaired());

            await RunUntilAsync(service, loopIterated.Task);

            await hubConnection.DidNotReceive().SendHeartbeatAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            await hubConnection.DisposeAsync();
        }
    }

    [Test]
    public async Task ExecuteAsync_WhenSendThrows_DoesNotCrash()
    {
        HeartbeatBackgroundService.TestDelayOverride = TimeSpan.FromMilliseconds(10);
        var hubConnection = CreateHubConnection(WorkerConnectionState.Connected);

        try
        {
            var heartbeatAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            hubConnection.SendHeartbeatAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                         .Returns(_ =>
                         {
                             heartbeatAttempted.TrySetResult();
                             return Task.FromException(new InvalidOperationException("boom"));
                         });

            using var service = CreateService(hubConnection, MockTokenStore.Paired("token", Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(1)));

            await RunUntilAsync(service, heartbeatAttempted.Task);

            await hubConnection.Received().SendHeartbeatAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            await hubConnection.DisposeAsync();
        }
    }

    [Test]
    public async Task ExecuteAsync_WhenCapabilityRefreshDue_ReportsCapabilities()
    {
        HeartbeatBackgroundService.TestDelayOverride = TimeSpan.FromMilliseconds(10);
        HeartbeatBackgroundService.TestCapabilityRefreshIntervalOverride = TimeSpan.FromMilliseconds(10);
        var hubConnection = CreateHubConnection(WorkerConnectionState.Connected);
        var capabilityReporter = Substitute.For<ICapabilityReporter>();
        var capabilitiesReported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        capabilityReporter.ReportToApiAsync(Arg.Any<CancellationToken>())
                          .Returns(Task.CompletedTask)
                          .AndDoes(_ => capabilitiesReported.TrySetResult());

        try
        {
            using var service = CreateService(hubConnection,
                MockTokenStore.Paired("token", Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(1)),
                capabilityReporter);

            await RunUntilAsync(service, capabilitiesReported.Task);

            await capabilityReporter.Received().ReportToApiAsync(Arg.Any<CancellationToken>());
        }
        finally
        {
            await hubConnection.DisposeAsync();
        }
    }

    [Test]
    public async Task ExecuteAsync_CancellationToken_StopsLoop()
    {
        HeartbeatBackgroundService.TestDelayOverride = TimeSpan.FromMilliseconds(10);
        var loopIterated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hubConnection = CreateHubConnection(WorkerConnectionState.Connected, loopIterated);

        try
        {
            using var service = CreateService(hubConnection, MockTokenStore.Paired("token", Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(1)));

            // RunUntilAsync only returns once ExecuteAsync has completed, so cancelling a loop that is demonstrably
            // running and observing it unwind is the whole assertion.
            await RunUntilAsync(service, loopIterated.Task);
        }
        finally
        {
            await hubConnection.DisposeAsync();
        }
    }

    /// <summary>
    ///     Runs the service until the loop raises <paramref name="signal" />, then cancels it and waits for
    ///     <c>ExecuteAsync</c> to unwind.
    /// </summary>
    private static async Task RunUntilAsync(BackgroundService service, Task signal)
    {
        using var cancellationTokenSource = new CancellationTokenSource();
        var execution = BackgroundServiceTestHelper.RunExecuteAsync(service, cancellationTokenSource.Token);

        try
        {
            await signal.WaitAsync(SignalTimeout);
        }
        finally
        {
            // Also runs when the signal times out, so a genuinely broken loop is stopped and drained rather than left
            // spinning against a disposed token source.
            await cancellationTokenSource.CancelAsync();
            await execution;
        }
    }

    private static IWorkerHubConnection CreateHubConnection(WorkerConnectionState state)
    {
        var hubConnection = Substitute.For<IWorkerHubConnection>();
        hubConnection.State.Returns(state);
        return hubConnection;
    }

    /// <summary>
    ///     Signals <paramref name="loopIterated" /> once the loop has read <c>State</c> twice. The second read can only
    ///     happen after the first iteration ran to completion, which is what makes a <c>DidNotReceive</c> assertion
    ///     evidence that the heartbeat was skipped rather than evidence that the loop never got to run.
    /// </summary>
    private static IWorkerHubConnection CreateHubConnection(WorkerConnectionState state, TaskCompletionSource loopIterated)
    {
        var hubConnection = Substitute.For<IWorkerHubConnection>();
        var reads = 0;
        hubConnection.State.Returns(_ =>
        {
            if (Interlocked.Increment(ref reads) >= 2)
            {
                loopIterated.TrySetResult();
            }

            return state;
        });

        return hubConnection;
    }

    private static HeartbeatBackgroundService CreateService(IWorkerHubConnection hubConnection,
        MockTokenStore tokenStore,
        ICapabilityReporter? capabilityReporter = null)
    {
        return new HeartbeatBackgroundService(hubConnection,
            tokenStore,
            new Lazy<ICapabilityReporter>(() => capabilityReporter ?? Substitute.For<ICapabilityReporter>()),
            Options.Create(new CentralPlatformOptions
            {
                BaseUrl = "https://test.example.com",
                HeartbeatIntervalSeconds = 1
            }),
            NullLogger<HeartbeatBackgroundService>.Instance);
    }
}

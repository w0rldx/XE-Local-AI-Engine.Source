namespace XE_Local_AI_Engine.Tests.BackgroundServices;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.BackgroundServices;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Services.Capabilities;
using XE_Local_AI_Engine.Client.Services.Connection;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Mocks;

public sealed class HeartbeatBackgroundServiceTests : IDisposable
{
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
            using var cancellationTokenSource = new CancellationTokenSource();
            var heartbeatSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            hubConnection.SendHeartbeatAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                         .Returns(Task.CompletedTask)
                         .AndDoes(_ => heartbeatSent.TrySetResult());
            cancellationTokenSource.CancelAfter(TimeSpan.FromSeconds(2));

            await BackgroundServiceTestHelper.RunExecuteAsync(service, cancellationTokenSource.Token);

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
        var hubConnection = CreateHubConnection(WorkerConnectionState.Disconnected);

        try
        {
            using var service = CreateService(hubConnection, MockTokenStore.Paired("token", Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(1)));
            using var cancellationTokenSource = new CancellationTokenSource();
            cancellationTokenSource.CancelAfter(120);

            await BackgroundServiceTestHelper.RunExecuteAsync(service, cancellationTokenSource.Token);

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
        var hubConnection = CreateHubConnection(WorkerConnectionState.Connected);

        try
        {
            using var service = CreateService(hubConnection, MockTokenStore.Unpaired());
            using var cancellationTokenSource = new CancellationTokenSource();
            cancellationTokenSource.CancelAfter(120);

            await BackgroundServiceTestHelper.RunExecuteAsync(service, cancellationTokenSource.Token);

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
            using var cancellationTokenSource = new CancellationTokenSource();
            var heartbeatAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            hubConnection.SendHeartbeatAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                         .Returns(_ =>
                         {
                             heartbeatAttempted.TrySetResult();
                             return Task.FromException(new InvalidOperationException("boom"));
                         });

            using var service = CreateService(hubConnection, MockTokenStore.Paired("token", Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(1)));
            cancellationTokenSource.CancelAfter(TimeSpan.FromSeconds(2));

            await BackgroundServiceTestHelper.RunExecuteAsync(service, cancellationTokenSource.Token);

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
        capabilityReporter.ReportToApiAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        try
        {
            using var service = CreateService(hubConnection,
                MockTokenStore.Paired("token", Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(1)),
                capabilityReporter);
            using var cancellationTokenSource = new CancellationTokenSource();
            cancellationTokenSource.CancelAfter(TimeSpan.FromSeconds(2));

            await BackgroundServiceTestHelper.RunExecuteAsync(service, cancellationTokenSource.Token);

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
        var hubConnection = CreateHubConnection(WorkerConnectionState.Connected);

        try
        {
            using var service = CreateService(hubConnection, MockTokenStore.Paired("token", Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(1)));
            using var cancellationTokenSource = new CancellationTokenSource();
            cancellationTokenSource.CancelAfter(60);

            await BackgroundServiceTestHelper.RunExecuteAsync(service, cancellationTokenSource.Token);
        }
        finally
        {
            await hubConnection.DisposeAsync();
        }
    }

    private static IWorkerHubConnection CreateHubConnection(WorkerConnectionState state)
    {
        var hubConnection = Substitute.For<IWorkerHubConnection>();
        hubConnection.State.Returns(state);
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

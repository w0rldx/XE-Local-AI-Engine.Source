namespace XE_Local_AI_Engine.Tests.BackgroundServices;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.BackgroundServices;
using XE_Local_AI_Engine.Configuration;
using XE_Local_AI_Engine.Services.Connection;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Mocks;

public sealed class HeartbeatBackgroundServiceTests : IDisposable
{
    public void Dispose()
    {
        HeartbeatBackgroundService.TestDelayOverride = TimeSpan.Zero;
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
            cancellationTokenSource.CancelAfter(120);

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
            hubConnection.SendHeartbeatAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                .Returns(_ => throw new InvalidOperationException("boom"));

            using var service = CreateService(hubConnection, MockTokenStore.Paired("token", Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(1)));
            using var cancellationTokenSource = new CancellationTokenSource();
            cancellationTokenSource.CancelAfter(120);

            await BackgroundServiceTestHelper.RunExecuteAsync(service, cancellationTokenSource.Token);

            await hubConnection.Received().SendHeartbeatAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
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

    private static HeartbeatBackgroundService CreateService(IWorkerHubConnection hubConnection, MockTokenStore tokenStore)
    {
        return new HeartbeatBackgroundService(
            hubConnection,
            tokenStore,
            Options.Create(new CentralPlatformOptions { BaseUrl = "https://test.example.com", HeartbeatIntervalSeconds = 1 }),
            NullLogger<HeartbeatBackgroundService>.Instance);
    }
}

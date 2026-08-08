namespace XE_Local_AI_Engine.Tests.Connection;

using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Connection;
using XE_Local_AI_Engine.Client.Services.Connection.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Mocks;

public sealed class ConnectionControlServiceTests
{
    [Test]
    public async Task GetStatusAsync_WhenPaired_ReturnsConnectionControlsWithoutSecrets()
    {
        var tokenStore = MockTokenStore.PairedWithAutoConnectDisabled();
        var connectionState = new ConnectionState();
        var service = CreateService(connectionState, tokenStore);

        var status = await service.GetStatusAsync();

        AssertEx.Equal("disconnected", status.State);
        AssertEx.True(status.IsPaired);
        AssertEx.False(status.AutoConnectOnStart);
        AssertEx.True(status.CanConnect);
        AssertEx.False(status.CanDisconnect);
        AssertEx.True(status.CanEnableAutoConnect);
    }

    [Test]
    public async Task ConnectAsync_DelegatesToWorkerHubConnection()
    {
        var hubConnection = Substitute.For<IWorkerHubConnection>();
        var service = CreateService(new ConnectionState(), MockTokenStore.PairedWithAutoConnectDisabled(), hubConnection);

        await service.ConnectAsync();

        await hubConnection.Received(1).ConnectAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DisconnectAsync_DelegatesToWorkerHubConnection()
    {
        var hubConnection = Substitute.For<IWorkerHubConnection>();
        var service = CreateService(new ConnectionState(), MockTokenStore.Paired("token", Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours(1)), hubConnection);

        await service.DisconnectAsync();

        await hubConnection.Received(1).DisconnectAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SetAutoConnectAsync_WhenEnabled_PersistsPreferenceWithoutConnecting()
    {
        var tokenStore = MockTokenStore.PairedWithAutoConnectDisabled();
        var hubConnection = Substitute.For<IWorkerHubConnection>();
        var service = CreateService(new ConnectionState(), tokenStore, hubConnection);

        var status = await service.SetAutoConnectAsync(true);

        AssertEx.True(tokenStore.AutoConnectOnStart);
        AssertEx.True(status.AutoConnectOnStart);
        await hubConnection.DidNotReceive().ConnectAsync(Arg.Any<CancellationToken>());
        await hubConnection.DidNotReceive().DisconnectAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SetAutoConnectAsync_WhenDisabledWhileReconnecting_DisconnectsAndPersistsPreference()
    {
        var tokenStore = MockTokenStore.Paired("token", Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours(1));
        var connectionState = new ConnectionState();
        connectionState.TransitionTo(WorkerConnectionState.Reconnecting, "network drop");
        var hubConnection = Substitute.For<IWorkerHubConnection>();
        hubConnection.DisconnectAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            connectionState.TransitionTo(WorkerConnectionState.Disconnected);
            return Task.CompletedTask;
        });
        var service = CreateService(connectionState, tokenStore, hubConnection);

        var status = await service.SetAutoConnectAsync(false);

        AssertEx.False(tokenStore.AutoConnectOnStart);
        AssertEx.Equal("disconnected", status.State);
        await hubConnection.Received(1).DisconnectAsync(Arg.Any<CancellationToken>());
    }

    private static ConnectionControlService CreateService(ConnectionState connectionState, MockTokenStore tokenStore, IWorkerHubConnection? hubConnection = null)
    {
        return new ConnectionControlService(connectionState,
            hubConnection ?? Substitute.For<IWorkerHubConnection>(),
            tokenStore);
    }
}

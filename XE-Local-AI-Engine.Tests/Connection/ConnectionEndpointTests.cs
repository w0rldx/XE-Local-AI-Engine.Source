namespace XE_Local_AI_Engine.Tests.Connection;

using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Endpoints.Connection.V1;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Connection;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Mocks;

public sealed class ConnectionEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Test]
    public async Task ConnectionActions_WhenInvoked_ReturnUpdatedStatusAndDelegateToWorkerConnection()
    {
        var tokenStore = MockTokenStore.PairedWithAutoConnectDisabled();
        var connectionState = new ConnectionState();
        var hubConnection = Substitute.For<IWorkerHubConnection>();
        hubConnection.ConnectAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            connectionState.TransitionTo(WorkerConnectionState.Connected);
            return Task.CompletedTask;
        });
        hubConnection.DisconnectAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            connectionState.TransitionTo(WorkerConnectionState.Disconnected);
            return Task.CompletedTask;
        });
        await using var factory = CreateFactory(tokenStore, connectionState, hubConnection);
        using var client = factory.CreateClient();

        using var statusRequest = CreateRequest(factory, HttpMethod.Get, "/api/local/v1/connection");
        using var connectRequest = CreateRequest(factory, HttpMethod.Post, "/api/local/v1/connection/connect");
        using var enableRequest = CreateRequest(factory, HttpMethod.Post, "/api/local/v1/connection/auto-connect/enable");
        using var disconnectRequest = CreateRequest(factory, HttpMethod.Post, "/api/local/v1/connection/disconnect");
        using var statusResponse = await client.SendAsync(statusRequest).ConfigureAwait(false);
        using var connectResponse = await client.SendAsync(connectRequest).ConfigureAwait(false);
        using var enableResponse = await client.SendAsync(enableRequest).ConfigureAwait(false);
        using var disconnectResponse = await client.SendAsync(disconnectRequest).ConfigureAwait(false);

        var status = await ReadJsonAsync<ConnectionStatusResponse>(statusResponse).ConfigureAwait(false);
        var connected = await ReadJsonAsync<ConnectionStatusResponse>(connectResponse).ConfigureAwait(false);
        var enabled = await ReadJsonAsync<ConnectionStatusResponse>(enableResponse).ConfigureAwait(false);
        var disconnected = await ReadJsonAsync<ConnectionStatusResponse>(disconnectResponse).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
        AssertEx.Equal(HttpStatusCode.OK, connectResponse.StatusCode);
        AssertEx.Equal(HttpStatusCode.OK, enableResponse.StatusCode);
        AssertEx.Equal(HttpStatusCode.OK, disconnectResponse.StatusCode);
        AssertEx.Equal("disconnected", status.State);
        AssertEx.True(status.CanConnect);
        AssertEx.False(status.CanDisconnect);
        AssertEx.Equal("connected", connected.State);
        AssertEx.True(connected.CanDisconnect);
        AssertEx.True(enabled.AutoConnectOnStart);
        AssertEx.Equal("disconnected", disconnected.State);
        await hubConnection.Received(1).ConnectAsync(Arg.Any<CancellationToken>());
        await hubConnection.Received(1).DisconnectAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DisableAutoConnect_WhenReconnecting_DisconnectsAndReturnsTerminalManualState()
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
        await using var factory = CreateFactory(tokenStore, connectionState, hubConnection);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Post, "/api/local/v1/connection/auto-connect/disable");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var status = await ReadJsonAsync<ConnectionStatusResponse>(response).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.False(tokenStore.AutoConnectOnStart);
        AssertEx.Equal("disconnected", status.State);
        AssertEx.True(status.CanConnect);
        AssertEx.True(status.CanEnableAutoConnect);
        AssertEx.False(status.CanDisableAutoConnect);
        await hubConnection.Received(1).DisconnectAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Connect_WhenWorkerNotPaired_ReturnsConflictFromGlobalHandlerNotFlattened400()
    {
        // Regression: the Connect endpoint no longer wraps the call in a catch-all that flattened every fault into a
        // 400 and leaked the raw message. A WorkerNotPairedException now flows to the global ConflictExceptionHandler,
        // which maps it to a 409 carrying the discriminating conflictType and the exception's user-safe message.
        var tokenStore = MockTokenStore.PairedWithAutoConnectDisabled();
        var connectionState = new ConnectionState();
        var hubConnection = Substitute.For<IWorkerHubConnection>();
        hubConnection.ConnectAsync(Arg.Any<CancellationToken>()).Returns<Task>(_ => throw new WorkerNotPairedException());
        await using var factory = CreateFactory(tokenStore, connectionState, hubConnection);
        using var client = factory.CreateClient();

        using var connectRequest = CreateRequest(factory, HttpMethod.Post, "/api/local/v1/connection/connect");
        using var connectResponse = await client.SendAsync(connectRequest).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Conflict, connectResponse.StatusCode);
        using var document = JsonDocument.Parse(await connectResponse.Content.ReadAsStringAsync().ConfigureAwait(false));
        AssertEx.Equal("WorkerNotPaired", document.RootElement.GetProperty("conflictType").GetString());
    }

    [Test]
    public async Task Connect_WhenWorkerTokenExpired_ReturnsConflictFromGlobalHandler()
    {
        var tokenStore = MockTokenStore.PairedWithAutoConnectDisabled();
        var connectionState = new ConnectionState();
        var hubConnection = Substitute.For<IWorkerHubConnection>();
        hubConnection.ConnectAsync(Arg.Any<CancellationToken>()).Returns<Task>(_ => throw new WorkerTokenExpiredException());
        await using var factory = CreateFactory(tokenStore, connectionState, hubConnection);
        using var client = factory.CreateClient();

        using var connectRequest = CreateRequest(factory, HttpMethod.Post, "/api/local/v1/connection/connect");
        using var connectResponse = await client.SendAsync(connectRequest).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Conflict, connectResponse.StatusCode);
        using var document = JsonDocument.Parse(await connectResponse.Content.ReadAsStringAsync().ConfigureAwait(false));
        AssertEx.Equal("WorkerTokenExpired", document.RootElement.GetProperty("conflictType").GetString());
    }

    private static TestServerWebAppFactory CreateFactory(MockTokenStore tokenStore, ConnectionState connectionState, IWorkerHubConnection hubConnection)
    {
        return new TestServerWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<ITokenStore>();
                services.AddSingleton<ITokenStore>(tokenStore);
                services.RemoveAll<ConnectionState>();
                services.AddSingleton(connectionState);
                services.RemoveAll<IWorkerHubConnection>();
                services.AddSingleton(hubConnection);
            }
        };
    }

    private static HttpRequestMessage CreateRequest(TestServerWebAppFactory factory, HttpMethod method, string uri)
    {
        var request = new HttpRequestMessage(method, uri);
        factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");
        return request;
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response)
        where T : class
    {
        await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        return AssertEx.NotNull(await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions).ConfigureAwait(false));
    }
}

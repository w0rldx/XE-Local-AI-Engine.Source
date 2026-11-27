namespace XE_Local_AI_Engine.Tests.Auth.Integration;

using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Security;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The hub socket on real Kestrel: the browser shape authenticates by the bearer subprotocol and gets the marker echoed,
///     and a header client offering no subprotocol gets none back and a working socket.
/// </summary>
/// <remarks>
///     Only a real server proves <c>UseWebSockets</c> runs before authentication: TestHost installs its own WebSocket
///     feature, so <see cref="HubBearerSubprotocolTests" /> stays green when the order breaks and this suite does not.
/// </remarks>
[Category(TestCategories.Integration)]
public sealed class HubBearerSubprotocolKestrelTests
{
    private const string Marker = NodeJwtBearerEvents.HubBearerProtocol;

    [ClassDataSource<KestrelHost>(Shared = SharedType.PerClass)]
    public required KestrelHost Host { get; init; }

    [Test]
    public async Task BrowserShapedSocket_IsAcceptedAndEchoesTheMarker()
    {
        var factory = Host.Factory;
        var token = factory.CreateNodeAccessToken();
        using var timeout = new CancellationTokenSource(TestBudgets.Contended);
        using var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol(Marker);
        socket.Options.AddSubProtocol(token);
        socket.Options.CollectHttpResponseDetails = true;

        await socket.ConnectAsync(await TransportUriAsync(factory, token, timeout.Token), timeout.Token);

        AssertEx.Equal(HttpStatusCode.SwitchingProtocols, socket.HttpStatusCode, "The browser-shaped upgrade must be accepted.");
        AssertEx.Equal(Marker, socket.SubProtocol, "The node must echo the marker, never the token.");
        AssertEx.True(HasSubprotocolResponseHeader(socket), "The 101 must carry Sec-WebSocket-Protocol, or a browser fails the socket.");
        await AssertHandshakeAnsweredAsync(socket, timeout.Token);
    }

    [Test]
    public async Task HeaderClientOfferingNoSubprotocol_GetsNoneBackAndAWorkingSocket()
    {
        var factory = Host.Factory;
        var token = factory.CreateNodeAccessToken();
        using var timeout = new CancellationTokenSource(TestBudgets.Contended);
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {token}");
        socket.Options.CollectHttpResponseDetails = true;

        await socket.ConnectAsync(await TransportUriAsync(factory, token, timeout.Token), timeout.Token);

        AssertEx.Equal(HttpStatusCode.SwitchingProtocols, socket.HttpStatusCode, "The header client's upgrade must be accepted.");
        AssertEx.Null(socket.SubProtocol, "A client that offered no subprotocol must be given none.");
        AssertEx.False(HasSubprotocolResponseHeader(socket), "The 101 must carry no Sec-WebSocket-Protocol for a client that offered none.");
        await AssertHandshakeAnsweredAsync(socket, timeout.Token);
    }

    /// <summary>Negotiates v1 with the header, as the browser does, and returns the socket URI for its connection token.</summary>
    private static async Task<Uri> TransportUriAsync(TestServerWebAppFactory factory, string token, CancellationToken cancellationToken)
    {
        var address = factory.KestrelAddress;
        using var client = new HttpClient
        {
            BaseAddress = address
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"{LocalApiRoutes.LocalChat.Hub}/negotiate?negotiateVersion=1", UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request, cancellationToken);
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode, "Negotiate with the header must succeed.");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var connectionToken = AssertEx.NotNull(body.RootElement.GetProperty("connectionToken").GetString(), "Negotiate v1 must return a connection token.");

        return new Uri($"ws://{address.Authority}{LocalApiRoutes.LocalChat.Hub}?id={Uri.EscapeDataString(connectionToken)}");
    }

    private static bool HasSubprotocolResponseHeader(ClientWebSocket socket) =>
        AssertEx.NotNull(socket.HttpResponseHeaders, "CollectHttpResponseDetails must expose the 101 headers.")
                .Keys.Any(static name => string.Equals(name, "Sec-WebSocket-Protocol", StringComparison.OrdinalIgnoreCase));

    private static async Task AssertHandshakeAnsweredAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        await socket.SendAsync(Encoding.UTF8.GetBytes("{\"protocol\":\"json\",\"version\":1}\u001e"), WebSocketMessageType.Text, endOfMessage: true,
            cancellationToken);
        var buffer = new byte[1024];
        var received = new List<byte>();
        int separator;
        // A read may end inside a record or carry several, so read up to the first record separator only.
        while ((separator = received.IndexOf(0x1e)) < 0)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            AssertEx.False(result.MessageType == WebSocketMessageType.Close, "The hub must answer the SignalR handshake before closing the socket.");
            received.AddRange(new ReadOnlySpan<byte>(buffer, 0, result.Count));
        }

        AssertEx.Equal("{}", Encoding.UTF8.GetString([.. received], 0, separator), "The hub must answer the SignalR handshake over the socket.");
    }

    /// <summary>One Kestrel-served host for the class; both tests only read.</summary>
    public sealed class KestrelHost : IAsyncDisposable
    {
        public TestServerWebAppFactory Factory { get; } = new()
        {
            ServeOnKestrel = true
        };

        public ValueTask DisposeAsync() =>
            Factory.DisposeAsync();
    }
}

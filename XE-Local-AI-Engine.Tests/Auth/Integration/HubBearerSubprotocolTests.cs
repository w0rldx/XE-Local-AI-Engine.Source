namespace XE_Local_AI_Engine.Tests.Auth.Integration;

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Security;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     A hub socket authenticates by <c>Sec-WebSocket-Protocol: xe.bearer, &lt;jwt&gt;</c> only on a genuine upgrade, only
///     in that exact shape and only without a <c>Bearer</c> header; a query <c>access_token</c> authenticates nothing.
/// </summary>
/// <remarks>
///     TestHost installs its own WebSocket feature, so this suite cannot prove where <c>UseWebSockets</c> sits in the
///     pipeline; <see cref="HubBearerSubprotocolKestrelTests" /> does that on real Kestrel.
/// </remarks>
[Category(TestCategories.Integration)]
public sealed class HubBearerSubprotocolTests
{
    private const string Marker = NodeJwtBearerEvents.HubBearerProtocol;
    private const string Refused = "Incomplete handshake, status code: 401";
    private const string Email = "admin@example.test";
    private const string Password = "Str0ng!Password123";
    private const string HubRoute = LocalApiRoutes.LocalChat.Hub;

    [ClassDataSource<TestServerWebAppFactory>(Shared = SharedType.PerClass)]
    public required TestServerWebAppFactory Factory { get; init; }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task BrowserShapedConnection_AuthenticatesBySubprotocol_AndInvokesAHubMethod(bool asOneHeaderField)
    {
        var factory = Factory;
        var token = factory.CreateNodeAccessToken();
        AssertEx.True(Regex.IsMatch(token, "^[A-Za-z0-9_-]+\\.[A-Za-z0-9_-]+\\.[A-Za-z0-9_-]+$", RegexOptions.None, TimeSpan.FromSeconds(5)),
            "A node-minted token must be a valid WebSocket subprotocol element, or the browser constructor throws.");
        Uri? socketUri = null;
        WebSocket? socket = null;

        await using var connection = new HubConnectionBuilder()
                                     .WithUrl("http://localhost" + HubRoute, options =>
                                     {
                                         // Negotiate carries the header, as in the browser; the socket carries only the subprotocol.
                                         options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                                         options.AccessTokenProvider = () => Task.FromResult<string?>(token);
                                         options.Transports = HttpTransportType.WebSockets;
                                         options.WebSocketFactory = async (context, cancellationToken) =>
                                         {
                                             socketUri = context.Uri;
                                             var client = factory.Server.CreateWebSocketClient();
                                             if (asOneHeaderField)
                                             {
                                                 client.ConfigureRequest = request => request.Headers.SecWebSocketProtocol = $"{Marker}, {token}";
                                             }
                                             else
                                             {
                                                 client.SubProtocols.Add(Marker);
                                                 client.SubProtocols.Add(token);
                                             }

                                             socket = await client.ConnectAsync(context.Uri, cancellationToken);
                                             return socket;
                                         };
                                     })
                                     .WithNodeJsonProtocol()
                                     .Build();

        await connection.StartAsync();
        var events = await connection.StreamAsync<ChatStreamEvent>("ResumeConversation", Guid.NewGuid()).ToListAsync();

        AssertEx.Equal(0, events.Count, "No turn is running, so the resume stream must be empty.");
        AssertEx.Equal(Marker, AssertEx.NotNull(socket).SubProtocol, "The node must echo the marker, never the token.");
        var query = AssertEx.NotNull(socketUri).Query;
        AssertEx.True(query.Contains("id=", StringComparison.Ordinal), "The socket must open a negotiated connection.");
        AssertEx.False(query.Contains("access_token", StringComparison.Ordinal), "The socket URL must carry no token.");
    }

    [Test]
    public async Task Upgrade_WithNoCredential_IsUnauthorized()
    {
        await AssertUpgradeRefusedAsync(Factory, HubRoute, configure: null, "An upgrade without any credential must be refused.");
    }

    [Test]
    public async Task Upgrade_WithOnlyAQueryAccessToken_IsUnauthorized()
    {
        var factory = Factory;
        var route = $"{HubRoute}?access_token={Uri.EscapeDataString(factory.CreateNodeAccessToken())}";

        await AssertUpgradeRefusedAsync(factory, route, configure: null, "A query access_token must authenticate nothing.");
    }

    [Test]
    [Arguments("xe.bearer")]
    [Arguments("xe.bearer, {0}, extra")]
    [Arguments("{0}, xe.bearer")]
    [Arguments("\"xe.bearer\", {0}")]
    [Arguments("xe.bearer, \"{0}\"")]
    [Arguments("xe.bearer, ")]
    [Arguments("xe.bearer, , {0}")]
    [Arguments("xe.bearer, {0}, {0}")]
    public async Task Upgrade_WhenTheSubprotocolIsNotExactlyMarkerThenToken_IsUnauthorized(string headerTemplate)
    {
        var factory = Factory;
        var value = string.Format(CultureInfo.InvariantCulture, headerTemplate, factory.CreateNodeAccessToken());

        await AssertUpgradeRefusedAsync(factory, HubRoute, request => request.Headers.SecWebSocketProtocol = value,
            "Only the raw, unquoted two-element list [xe.bearer, <jwt>] may authenticate.");
    }

    [Test]
    public async Task Upgrade_WithAValidBearerHeaderAndAGarbageSubprotocol_IsAccepted()
    {
        var factory = Factory;
        var token = factory.CreateNodeAccessToken();

        using var socket = await UpgradeAsync(factory, HubRoute, request => request.Headers.Authorization = $"Bearer {token}", Marker, "not-a-token");

        AssertEx.Equal(WebSocketState.Open, socket.State, "A Bearer header wins over the subprotocol, whatever it holds.");
    }

    [Test]
    public async Task Upgrade_WithAnInvalidBearerHeaderAndAValidSubprotocol_IsUnauthorized()
    {
        var factory = Factory;
        var token = factory.CreateNodeAccessToken();

        await AssertUpgradeRefusedAsync(factory, HubRoute, request => request.Headers.Authorization = "Bearer not-a-token",
            "An invalid Bearer header must never fall back to the subprotocol.", Marker, token);
    }

    [Test]
    public async Task Upgrade_WithABareBearerHeaderAndAValidSubprotocol_IsUnauthorized()
    {
        var factory = Factory;
        var token = factory.CreateNodeAccessToken();

        await AssertUpgradeRefusedAsync(factory, HubRoute, request => request.Headers.Authorization = "Bearer",
            "A Bearer scheme without a credential must never fall back to the subprotocol.", Marker, token);
    }

    [Test]
    public async Task Upgrade_WithABasicHeaderAndAValidSubprotocol_IsAccepted()
    {
        var factory = Factory;
        var token = factory.CreateNodeAccessToken();

        using var socket = await UpgradeAsync(factory, HubRoute, request => request.Headers.Authorization = "Basic dXNlcjpwYXNz", Marker, token);

        AssertEx.Equal(WebSocketState.Open, socket.State, "Only a Bearer header suppresses the subprotocol; a proxy's Basic header does not.");
    }

    [Test]
    public async Task LongPolling_WithOnlyTheSubprotocol_IsUnauthorizedOnPollSendAndDelete()
    {
        var factory = Factory;
        var token = factory.CreateNodeAccessToken();
        using var client = factory.CreateClient();
        var transport = $"{HubRoute}?id={Uri.EscapeDataString(await NegotiateAsync(client, token))}";

        // Each refusal precedes its header-authenticated twin, so the twin proves the request itself was well formed.
        AssertEx.Equal(HttpStatusCode.Unauthorized, await SendWithSubprotocolAsync(client, HttpMethod.Get, transport, token), "A long poll is not an upgrade.");
        AssertEx.Equal(HttpStatusCode.OK, await SendWithBearerAsync(client, HttpMethod.Get, transport, token), "The header client's first poll.");
        AssertEx.Equal(HttpStatusCode.Unauthorized, await SendWithSubprotocolAsync(client, HttpMethod.Post, transport, token), "A send is not an upgrade.");
        AssertEx.Equal(HttpStatusCode.OK, await SendWithBearerAsync(client, HttpMethod.Post, transport, token), "The header client's handshake send.");
        AssertEx.Equal(HttpStatusCode.Unauthorized, await SendWithSubprotocolAsync(client, HttpMethod.Delete, transport, token), "A DELETE is not an upgrade.");
        AssertEx.Equal(HttpStatusCode.Accepted, await SendWithBearerAsync(client, HttpMethod.Delete, transport, token), "The header client's DELETE.");
    }

    [Test]
    public async Task NegotiateAndRestRoutes_WithOnlyTheSubprotocol_AreUnauthorized()
    {
        var factory = Factory;
        var token = factory.CreateNodeAccessToken();
        using var client = factory.CreateClient();
        var restRoute = $"/{LocalApiRoutes.Prefix}/{LocalApiRoutes.Agents.Definitions}";

        AssertEx.Equal(HttpStatusCode.Unauthorized, await SendWithSubprotocolAsync(client, HttpMethod.Post, $"{HubRoute}/negotiate?negotiateVersion=1", token),
            "Negotiate authenticates only by the header.");
        AssertEx.Equal(HttpStatusCode.Unauthorized, await SendWithSubprotocolAsync(client, HttpMethod.Get, restRoute, token),
            "A REST route never reads the subprotocol.");
        AssertEx.Equal(HttpStatusCode.OK, await SendWithBearerAsync(client, HttpMethod.Get, restRoute, token), "The same REST route with the header.");
    }

    [Test]
    public async Task RestRouteEndingInHub_WithAQueryAccessToken_IsUnauthorized()
    {
        var factory = Factory;
        using var client = factory.CreateClient();
        var route = $"/{LocalApiRoutes.Prefix}/agents/hub?access_token={Uri.EscapeDataString(factory.CreateNodeAccessToken())}";

        using var response = await client.GetAsync(new Uri(route, UriKind.Relative));

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode, "A path ending in /hub is not a hub, and a query token authenticates nothing.");
    }

    [Test]
    public async Task Upgrade_WithARevokedSessionsToken_IsUnauthorized()
    {
        await using var factory = new TestServerWebAppFactory();
        var revoked = await SetupAndLoginAsync(factory);
        var other = await LoginAsync(factory);
        using (var before = await UpgradeAsync(factory, HubRoute, configure: null, Marker, revoked))
        {
            AssertEx.Equal(WebSocketState.Open, before.State, "The token opens a socket before its session ends, so the refusal below is the revocation's.");
        }

        using (var client = factory.CreateClient())
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, new Uri($"/api/local/v1/auth/sessions/{SessionId(revoked)}", UriKind.Relative));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", other);
            using var response = await client.SendAsync(request);
            AssertEx.Equal(HttpStatusCode.NoContent, response.StatusCode, "The session revoke must succeed.");
        }

        await AssertUpgradeRefusedAsync(factory, HubRoute, configure: null, "A revoked session's token must not open a socket.", Marker, revoked);
    }

    [Test]
    public async Task Upgrade_WithATokenFromBeforeAPasswordChange_IsUnauthorized()
    {
        await using var factory = new TestServerWebAppFactory();
        var login = await SetupAndLoginAsync(factory);
        var legacy = await MintSessionlessAccessTokenAsync(factory);
        using (var before = await UpgradeAsync(factory, HubRoute, configure: null, Marker, legacy))
        {
            AssertEx.Equal(WebSocketState.Open, before.State, "The token opens a socket before the stamp rotates, so the refusal below is the stamp check's.");
        }

        using (var client = factory.CreateClient())
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/local/v1/auth/change-password", UriKind.Relative))
            {
                Content = JsonContent.Create(new
                {
                    currentPassword = Password,
                    newPassword = Password + "Changed"
                })
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", login);
            using var response = await client.SendAsync(request);
            AssertEx.True(response.IsSuccessStatusCode, $"The password change must succeed, got {response.StatusCode}.");
        }

        await AssertUpgradeRefusedAsync(factory, HubRoute, configure: null, "A token minted before the password change must not open a socket.", Marker, legacy);
    }

    private static async Task<WebSocket> UpgradeAsync(TestServerWebAppFactory factory, string route, Action<HttpRequest>? configure, params string[] subProtocols)
    {
        var client = factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = configure;
        foreach (var subProtocol in subProtocols)
        {
            client.SubProtocols.Add(subProtocol);
        }

        return await client.ConnectAsync(new Uri("ws://localhost" + route), CancellationToken.None);
    }

    private static async Task AssertUpgradeRefusedAsync(TestServerWebAppFactory factory, string route, Action<HttpRequest>? configure, string because,
        params string[] subProtocols)
    {
        var refusal = await AssertEx.ThrowsAsync<InvalidOperationException>(() => UpgradeAsync(factory, route, configure, subProtocols), because);
        AssertEx.Equal(Refused, refusal.Message, because);
    }

    private static async Task<string> NegotiateAsync(HttpClient client, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"{HubRoute}/negotiate?negotiateVersion=1", UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode, "Negotiate with the header must succeed.");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return AssertEx.NotNull(body.RootElement.GetProperty("connectionToken").GetString(), "Negotiate v1 must return a connection token.");
    }

    private static Task<HttpStatusCode> SendWithSubprotocolAsync(HttpClient client, HttpMethod method, string route, string token) =>
        SendAsync(client, method, route, request => request.Headers.TryAddWithoutValidation("Sec-WebSocket-Protocol", $"{Marker}, {token}"));

    private static Task<HttpStatusCode> SendWithBearerAsync(HttpClient client, HttpMethod method, string route, string token) =>
        SendAsync(client, method, route, request => request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token));

    private static async Task<HttpStatusCode> SendAsync(HttpClient client, HttpMethod method, string route, Action<HttpRequestMessage> authenticate)
    {
        using var request = new HttpRequestMessage(method, new Uri(route, UriKind.Relative));
        if (method == HttpMethod.Post)
        {
            // A SignalR handshake frame: a well-formed send, so only authentication decides the status.
            request.Content = new StringContent("{\"protocol\":\"json\",\"version\":1}\u001e", Encoding.UTF8);
        }

        authenticate(request);
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    private static async Task<string> SetupAndLoginAsync(TestServerWebAppFactory factory)
    {
        using (var client = factory.CreateClient())
        {
            using var response = await client.PostAsJsonAsync(new Uri("/api/local/v1/auth/setup", UriKind.Relative),
                new
                {
                    email = Email,
                    password = Password
                });
            AssertEx.Equal(HttpStatusCode.OK, response.StatusCode, "The node setup must succeed.");
        }

        return await LoginAsync(factory);
    }

    private static async Task<string> LoginAsync(TestServerWebAppFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var login = await scope.ServiceProvider.GetRequiredService<INodeAuthService>()
                               .LoginAsync(Email, Password, clientLabel: null, CancellationToken.None);
        AssertEx.True(login.Succeeded, "The fixture's login must succeed.");
        return AssertEx.NotNull(login.AccessToken);
    }

    /// <summary>Mints an access token without <c>sid</c>, so only the security stamp check can refuse it.</summary>
    private static async Task<string> MintSessionlessAccessTokenAsync(TestServerWebAppFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var admin = AssertEx.NotNull(await scope.ServiceProvider.GetRequiredService<INodeIdentityStore>()
                                                .FindCompletedSetupUserAsync(CancellationToken.None));
        return scope.ServiceProvider.GetRequiredService<INodeTokenService>()
                    .CreateAccessToken(admin, [NodeAuthorizationPolicies.AdminRole]).AccessToken;
    }

    private static string SessionId(string accessToken) =>
        new JsonWebToken(accessToken).GetClaim(JwtRegisteredClaimNames.Sid).Value;
}

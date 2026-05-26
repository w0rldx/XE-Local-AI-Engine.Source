namespace XE_Local_AI_Engine.Tests.Auth.Integration;

using System.Net;
using System.Net.Http.Headers;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class LocalChatHubAuthorizationIntegrationTests
{
    // The hub connection path itself. The JwtBearer OnMessageReceived handler only reads the
    // access_token query parameter for request paths ending in "/hub" (see ConfigureServices).
    private const string HubRoute = "/api/local/v1/chat/hub";

    // SignalR exposes the negotiate handshake at <hub-route>/negotiate. The negotiate path does
    // NOT end in "/hub", so it authenticates via the standard Bearer header, not access_token.
    private const string HubNegotiateRoute = "/api/local/v1/chat/hub/negotiate";

    [Test]
    public async Task HubNegotiate_WhenNoCredentials_ReturnsUnauthorized()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, HubNegotiateRoute);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task HubNegotiate_WhenBearerHeaderProvided_ReturnsOk()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, HubNegotiateRoute);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.CreateNodeAccessToken());
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Test]
    public async Task HubConnect_WhenNoAccessTokenQuery_ReturnsUnauthorized()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, HubRoute);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task HubConnect_WhenAccessTokenSuppliedViaQueryString_PassesAuthorization()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        var accessToken = factory.CreateNodeAccessToken();
        var route = $"{HubRoute}?access_token={Uri.EscapeDataString(accessToken)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, route);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        // The access_token query token is accepted by the JwtBearer handler, so authorization
        // passes. SignalR then rejects the non-WebSocket GET at the transport layer, but it must
        // never surface as 401 Unauthorized once a valid access_token query token is supplied.
        AssertEx.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}

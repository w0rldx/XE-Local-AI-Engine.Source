namespace XE_Local_AI_Engine.Tests.Auth.Integration;

using System.Net;
using System.Net.Http.Headers;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Integration)]
public sealed class LocalChatHubAuthorizationIntegrationTests
{
    [ClassDataSource<TestServerWebAppFactory>(Shared = SharedType.PerClass)]
    public required TestServerWebAppFactory Factory { get; init; }

    // The hub transport endpoint. NodeJwtBearerEvents reads a token there only from the bearer subprotocol of a genuine
    // WebSocket upgrade, never from the access_token query.
    private const string HubRoute = "/api/local/v1/chat/hub";

    // SignalR exposes the negotiate handshake at <hub-route>/negotiate, which authenticates only by the Bearer header.
    private const string HubNegotiateRoute = "/api/local/v1/chat/hub/negotiate";

    [Test]
    public async Task HubNegotiate_WhenNoCredentials_ReturnsUnauthorized()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, HubNegotiateRoute);
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task HubNegotiate_WhenBearerHeaderProvided_ReturnsOk()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, HubNegotiateRoute);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.CreateNodeAccessToken());
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Test]
    public async Task HubConnect_WhenNoAccessTokenQuery_ReturnsUnauthorized()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, HubRoute);
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task HubConnect_WhenAccessTokenSuppliedViaQueryString_IsUnauthorized()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        var accessToken = factory.CreateNodeAccessToken();
        var route = $"{HubRoute}?access_token={Uri.EscapeDataString(accessToken)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, route);
        using var response = await client.SendAsync(request);

        // A query token authenticates nothing: it is the URL every failed socket line would print.
        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}

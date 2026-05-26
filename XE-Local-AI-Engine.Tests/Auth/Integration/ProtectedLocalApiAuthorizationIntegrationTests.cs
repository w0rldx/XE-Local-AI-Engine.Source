namespace XE_Local_AI_Engine.Tests.Auth.Integration;

using System.Net;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class ProtectedLocalApiAuthorizationIntegrationTests
{
    // A non-auth local API endpoint guarded by the NodeOperator policy.
    private const string ProtectedConnectionStatusRoute = "/api/local/v1/connection";

    [Test]
    public async Task ProtectedEndpoint_WhenNoBearerTokenProvided_ReturnsUnauthorized()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, ProtectedConnectionStatusRoute);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task ProtectedEndpoint_WhenValidBearerTokenProvided_ReturnsOk()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, ProtectedConnectionStatusRoute);
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

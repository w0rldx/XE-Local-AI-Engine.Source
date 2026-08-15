namespace XE_Local_AI_Engine.Tests.Auth.Integration;

using System.Net;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class ProtectedLocalApiAuthorizationIntegrationTests
{
    // Both tests are read-only GETs against the same default host, so one bootstrap serves the class.
    [ClassDataSource<TestServerWebAppFactory>(Shared = SharedType.PerClass)]
    public required TestServerWebAppFactory Factory { get; init; }

    // A non-auth local API endpoint guarded by the NodeOperator policy.
    private const string ProtectedConnectionStatusRoute = "/api/local/v1/connection";

    [Test]
    public async Task ProtectedEndpoint_WhenNoBearerTokenProvided_ReturnsUnauthorized()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, ProtectedConnectionStatusRoute);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task ProtectedEndpoint_WhenValidBearerTokenProvided_ReturnsOk()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, ProtectedConnectionStatusRoute);
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

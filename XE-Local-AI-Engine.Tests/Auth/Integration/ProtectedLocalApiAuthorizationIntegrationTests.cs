namespace XE_Local_AI_Engine.Tests.Auth.Integration;

using System.Net;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Integration)]
public sealed class ProtectedLocalApiAuthorizationIntegrationTests
{
    // Both tests are read-only GETs against the same default host, so one bootstrap serves the class.
    [ClassDataSource<TestServerWebAppFactory>(Shared = SharedType.PerClass)]
    public required TestServerWebAppFactory Factory { get; init; }

    // A non-auth local API endpoint guarded by the NodeOperator policy. Read-only, so both tests can share one host.
    private const string ProtectedNodeSettingsRoute = "/api/local/v1/node-settings";

    [Test]
    public async Task ProtectedEndpoint_WhenNoBearerTokenProvided_ReturnsUnauthorized()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, ProtectedNodeSettingsRoute);
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task ProtectedEndpoint_WhenValidBearerTokenProvided_ReturnsOk()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, ProtectedNodeSettingsRoute);
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

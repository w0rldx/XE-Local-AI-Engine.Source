namespace XE_Local_AI_Engine.Tests.Endpoints.GraphWorkflows.V1;

using System.Net;
using System.Text.Json;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The one graph-workflow route the disabled-node 404 sweep must NOT swallow. Without it the SPA reads the bodyless
///     404 as a load failure: the workflow page says "could not load" and chat refuses to send.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class GraphWorkflowCapabilityEndpointTests
{
    private const string Root = "/api/local/v1/graph-workflows";

    [Test]
    [Arguments("false", false)]
    [Arguments("true", true)]
    public async Task Capability_ReportsTheSwitch_AndStaysReachableWhileTheRestOfTheFamilyIs404(string configured, bool expected)
    {
        await using var factory = new TestServerWebAppFactory
        {
            AdditionalConfiguration = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["GraphWorkflows:Enabled"] = configured
            }
        };

        using var response = await SendAsync(factory, $"{Root}/capability");
        var body = await response.Content.ReadAsStringAsync();

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode, "The capability GET answers on every node, switch on or off.");
        using var document = JsonDocument.Parse(body);
        AssertEx.Equal(expected, document.RootElement.GetProperty("enabled").GetBoolean());

        // The carve-out is exactly one path: its siblings must still be gone on a disabled node.
        using var sibling = await SendAsync(factory, $"{Root}/definitions");
        AssertEx.Equal(expected ? HttpStatusCode.OK : HttpStatusCode.NotFound,
            sibling.StatusCode,
            "Only the capability path is exempt from the disabled-node 404 sweep.");
    }

    [Test]
    public async Task Capability_WhenTheOperatorTokenIsMissing_ReturnsUnauthorized()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Root}/capability");

        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode, "The capability GET is operator-gated like the rest of the family.");
    }

    private static async Task<HttpResponseMessage> SendAsync(TestServerWebAppFactory factory, string route)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, route);
        factory.AddNodeBearerToken(request);
        return await client.SendAsync(request);
    }
}

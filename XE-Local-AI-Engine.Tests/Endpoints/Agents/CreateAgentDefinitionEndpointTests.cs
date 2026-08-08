namespace XE_Local_AI_Engine.Tests.Endpoints.Agents;

using System.Net;
using System.Net.Http.Json;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Guards the typed <c>Send.CreatedAtAsync&lt;GetAgentDefinitionEndpoint&gt;()</c> Location round-trip on
///     <c>POST agents</c>. This is one of the three create endpoints whose Location header is resolved by the target
///     endpoint's generated name; the global <c>Endpoints.NameGenerator</c> (Program.cs) must keep that resolution
///     working — if it regressed, the POST would throw (500) instead of returning 201 + a resolvable Location.
/// </summary>
public sealed class CreateAgentDefinitionEndpointTests
{
    [Test]
    public async Task Create_WhenValid_Returns201WithResolvableLocation()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/local/v1/agents")
        {
            Content = JsonContent.Create(new
            {
                name = "Location Round-Trip Agent",
                instructions = "You are a careful engineering agent."
            })
        };
        factory.AddNodeBearerToken(createRequest);

        using var createResponse = await client.SendAsync(createRequest).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        AssertEx.NotNull(createResponse.Headers.Location);

        // The Location must resolve to the GetAgentDefinitionEndpoint, proving CreatedAtAsync resolved the target
        // endpoint name through the NameGenerator.
        using var getRequest = new HttpRequestMessage(HttpMethod.Get, createResponse.Headers.Location);
        factory.AddNodeBearerToken(getRequest);

        using var getResponse = await client.SendAsync(getRequest).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, getResponse.StatusCode);
    }
}

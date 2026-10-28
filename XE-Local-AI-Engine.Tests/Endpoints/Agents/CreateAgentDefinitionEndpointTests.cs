namespace XE_Local_AI_Engine.Tests.Endpoints.Agents;

using System.Net;
using System.Net.Http.Json;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Guards the <c>Send.CreatedAtAsync&lt;GetAgentDefinitionEndpoint&gt;()</c> Location on <c>POST agents</c>: the
///     global <c>Endpoints.NameGenerator</c> must keep resolving the target's generated name, or the POST answers 500,
///     not 201.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class CreateAgentDefinitionEndpointTests
{
    [Test]
    public async Task Create_WhenValid_Returns201WithResolvableLocation()
    {
        await using var factory = new TestServerWebAppFactory();
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

        using var createResponse = await client.SendAsync(createRequest);

        AssertEx.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        AssertEx.NotNull(createResponse.Headers.Location);

        // The Location must resolve to the GetAgentDefinitionEndpoint, proving CreatedAtAsync resolved the target
        // endpoint name through the NameGenerator.
        using var getRequest = new HttpRequestMessage(HttpMethod.Get, createResponse.Headers.Location);
        factory.AddNodeBearerToken(getRequest);

        using var getResponse = await client.SendAsync(getRequest);

        AssertEx.Equal(HttpStatusCode.OK, getResponse.StatusCode);
    }
}

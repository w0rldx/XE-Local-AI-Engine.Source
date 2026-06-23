namespace XE_Local_AI_Engine.Tests.Endpoints.Agents;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class AnalyzePlaybookEndpointTests
{
    private static string Route(Guid agentDefinitionId)
    {
        return $"/api/local/v1/agents/{agentDefinitionId}/playbook/analyze";
    }

    [Test]
    public async Task Analyze_WhenNoBearerToken_ReturnsUnauthorized()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, Route(Guid.NewGuid()));
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task Analyze_WhenAgentUnknown_ReturnsNotFound()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, Route(Guid.NewGuid()))
        {
            // A JSON body is still accepted (the body-less shape is covered separately by Analyze_BodyLessPost_*).
            Content = JsonContent.Create(new
            {
            })
        };
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Test]
    public async Task Analyze_BodyLessPost_IsAcceptedNot415()
    {
        // Regression for the live 415 ("convert playbook → empty notification"): this route-only POST binds the agent
        // id from the route, so the hey-api client sends no body — and therefore no Content-Type. The endpoint must
        // accept that instead of answering 415 Unsupported Media Type. A seeded agent with no feedback yields 200 with
        // empty items, which proves the request was bound and dispatched rather than rejected at the media-type gate.
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        var agentId = await SeedAgentAsync(factory, "Body-less Analysis Agent").ConfigureAwait(false);

        // No HttpContent at all → the request carries no Content-Type header (the exact shape of a body-less fetch).
        using var request = new HttpRequestMessage(HttpMethod.Post, Route(agentId));
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.NotEqual(HttpStatusCode.UnsupportedMediaType, response.StatusCode, "Body-less analyze POST must not return 415.");
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var document = JsonDocument.Parse(payload);
        AssertEx.Equal(expected: 0, document.RootElement.GetProperty("items").GetArrayLength());
    }

    [Test]
    public async Task Analyze_WhenAgentExistsWithoutFeedback_ReturnsOkWithEmptyItems()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        var agentId = await SeedAgentAsync(factory, "Analysis Agent").ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Post, Route(agentId))
        {
            Content = JsonContent.Create(new
            {
            })
        };
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        // The agent has no feedback (below threshold), so the model is never invoked and no suggestion is created.
        AssertEx.Equal(expected: 0, root.GetProperty("items").GetArrayLength());
    }

    private static async Task<Guid> SeedAgentAsync(TestingWebAppFactory factory, string name)
    {
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IAgentDefinitionStore>();
        var agent = await store.AddAsync(new AgentDefinitionInput(name,
            Description: null,
            "You are a careful engineering agent.",
            ModelProfile: null,
            ReasoningEffort: null,
            AgentDefinitionKind.Single,
            [],
            new Dictionary<string, bool>(),
            OrchestrationTopologyJson: null)).ConfigureAwait(false);
        return agent.Id;
    }
}

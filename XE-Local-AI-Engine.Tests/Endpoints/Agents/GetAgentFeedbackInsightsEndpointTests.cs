namespace XE_Local_AI_Engine.Tests.Endpoints.Agents;

using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class GetAgentFeedbackInsightsEndpointTests
{
    private static string Route(Guid agentDefinitionId)
    {
        return $"/api/local/v1/agents/{agentDefinitionId}/feedback-insights";
    }

    [Test]
    public async Task GetFeedbackInsights_WhenNoBearerToken_ReturnsUnauthorized()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, Route(Guid.NewGuid()));
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task GetFeedbackInsights_WhenAgentUnknown_ReturnsNotFound()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, Route(Guid.NewGuid()));
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Test]
    public async Task GetFeedbackInsights_WhenAgentExistsWithoutFeedback_ReturnsOkEmptyState()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        var agentId = await SeedAgentAsync(factory, "Insights Agent").ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Get, Route(agentId));
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        AssertEx.Equal("Insights Agent", root.GetProperty("agentName").GetString());
        AssertEx.Equal(3, root.GetProperty("minOccurrenceThreshold").GetInt32());
        AssertEx.Equal(0, root.GetProperty("overall").GetProperty("total").GetInt32());
        AssertEx.False(root.GetProperty("overall").GetProperty("meetsThreshold").GetBoolean(), "An agent with no feedback is not an actionable pattern.");
        AssertEx.Equal(0, root.GetProperty("byTool").GetArrayLength());
        AssertEx.Equal(0, root.GetProperty("exemplars").GetArrayLength());
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
            AllowedToolNames: [],
            ToolApprovals: new Dictionary<string, bool>(),
            OrchestrationTopologyJson: null)).ConfigureAwait(false);
        return agent.Id;
    }
}

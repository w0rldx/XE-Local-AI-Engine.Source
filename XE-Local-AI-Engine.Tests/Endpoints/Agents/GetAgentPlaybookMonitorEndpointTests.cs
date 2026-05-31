namespace XE_Local_AI_Engine.Tests.Endpoints.Agents;

using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Playbook P5: the read-only playbook monitor endpoint. Operator-gated; 404 when the agent does not exist; 200 with
///     an empty <c>items</c> array for an agent with no Enabled actions, plus a <c>retrieval</c> block carrying the
///     relevance-gating thresholds.
/// </summary>
public sealed class GetAgentPlaybookMonitorEndpointTests
{
    private static string Route(Guid agentDefinitionId)
    {
        return $"/api/local/v1/agents/{agentDefinitionId}/playbook/monitor";
    }

    [Test]
    public async Task GetPlaybookMonitor_WhenNoBearerToken_ReturnsUnauthorized()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, Route(Guid.NewGuid()));
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task GetPlaybookMonitor_WhenAgentUnknown_ReturnsNotFound()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, Route(Guid.NewGuid()));
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Test]
    public async Task GetPlaybookMonitor_WhenAgentExistsWithoutEnabledActions_ReturnsOkEmptyItemsWithRetrieval()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        var agentId = await SeedAgentAsync(factory, "Monitor Agent").ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Get, Route(agentId));
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        AssertEx.Equal(0, root.GetProperty("items").GetArrayLength());

        var retrieval = root.GetProperty("retrieval");
        // Defaults from PlaybookRetrievalOptions (Section "PlaybookRetrieval"): RetrievalThreshold=8, TopK=8.
        AssertEx.Equal(8, retrieval.GetProperty("threshold").GetInt32());
        AssertEx.Equal(8, retrieval.GetProperty("topK").GetInt32());
    }

    private static async Task<Guid> SeedAgentAsync(TestingWebAppFactory factory, string name)
    {
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IAgentDefinitionStore>();
        var agent = await store.AddAsync(new AgentDefinitionInput(
            name,
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

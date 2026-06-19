namespace XE_Local_AI_Engine.Tests.Endpoints.Agents;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class UpdateSuggestedPlaybookActionEndpointTests
{
    private static string SuggestedRoute(Guid agentDefinitionId, Guid actionId)
    {
        return $"/api/local/v1/agents/{agentDefinitionId}/playbook/{actionId}/suggested";
    }

    [Test]
    public async Task UpdateSuggested_WhenNoBearerToken_ReturnsUnauthorized()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Put, SuggestedRoute(Guid.NewGuid(), Guid.NewGuid()))
        {
            Content = JsonContent.Create(new
            {
                behavior = "Edited behavior.",
                priority = 7
            })
        };
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task UpdateSuggested_WhenActionMissing_ReturnsNotFound()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        var agentId = await SeedAgentAsync(factory, "Owner").ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Put, SuggestedRoute(agentId, Guid.NewGuid()))
        {
            Content = JsonContent.Create(new
            {
                behavior = "Edited behavior.",
                priority = 7
            })
        };
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Test]
    public async Task UpdateSuggested_WhenActionBelongsToDifferentAgent_ReturnsNotFound()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        var ownerAgentId = await SeedAgentAsync(factory, "Owner").ConfigureAwait(false);
        var otherAgentId = await SeedAgentAsync(factory, "Other").ConfigureAwait(false);
        var seeded = await SeedSuggestionAsync(factory, ownerAgentId).ConfigureAwait(false);

        // Edit the owner's suggestion via the OTHER agent's route — the ownership guard must 404.
        using var request = new HttpRequestMessage(HttpMethod.Put, SuggestedRoute(otherAgentId, seeded.Id))
        {
            Content = JsonContent.Create(new
            {
                behavior = "Edited behavior.",
                priority = 7
            })
        };
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Test]
    public async Task UpdateSuggested_WhenBlankBehavior_ReturnsBadRequest()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        var agentId = await SeedAgentAsync(factory, "Owner").ConfigureAwait(false);
        var seeded = await SeedSuggestionAsync(factory, agentId).ConfigureAwait(false);

        // A blank Behavior is rejected by the service (PlaybookActionValidationException → 400 via Send.ErrorsAsync).
        using var request = new HttpRequestMessage(HttpMethod.Put, SuggestedRoute(agentId, seeded.Id))
        {
            Content = JsonContent.Create(new
            {
                behavior = "   ",
                priority = 7
            })
        };
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Test]
    public async Task UpdateSuggested_WhenOwnedPendingSuggestion_ReturnsOkWithEditedFieldsAndPreservedEvidence()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        var agentId = await SeedAgentAsync(factory, "Owner").ConfigureAwait(false);
        var seeded = await SeedSuggestionAsync(factory, agentId).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Put, SuggestedRoute(agentId, seeded.Id))
        {
            Content = JsonContent.Create(new
            {
                behavior = "Always link the failing test before reporting done.",
                triggerCondition = "On task completion.",
                scope = "testing",
                priority = 42
            })
        };
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        // The edit is applied, but the action stays a pending Analysis suggestion that keeps its evidence + confidence.
        AssertEx.Equal("Always link the failing test before reporting done.", root.GetProperty("behavior").GetString());
        AssertEx.Equal("On task completion.", root.GetProperty("triggerCondition").GetString());
        AssertEx.Equal("testing", root.GetProperty("scope").GetString());
        AssertEx.Equal(42, root.GetProperty("priority").GetInt32());
        AssertEx.Equal("Suggested", root.GetProperty("state").GetString());
        AssertEx.Equal("Analysis", root.GetProperty("source").GetString());

        var feedbackIds = root.GetProperty("sourceFeedbackIds");
        AssertEx.Equal(1, feedbackIds.GetArrayLength());
        AssertEx.Equal(seeded.SourceFeedbackIds![0], feedbackIds[0].GetGuid());
        AssertEx.True(root.GetProperty("confidence").GetDouble() > 0d, "Confidence must survive the edit.");
        AssertEx.Equal(seeded.Confidence!.Value, root.GetProperty("confidence").GetDouble());
    }

    private static async Task<Guid> SeedAgentAsync(TestingWebAppFactory factory, string name)
    {
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IAgentDefinitionStore>();
        var agent = await store.AddAsync(new AgentDefinitionInput(name,
            null,
            "You are a careful engineering agent.",
            null,
            null,
            AgentDefinitionKind.Single,
            [],
            new Dictionary<string, bool>(),
            null)).ConfigureAwait(false);
        return agent.Id;
    }

    private static async Task<PlaybookActionRecord> SeedSuggestionAsync(TestingWebAppFactory factory, Guid agentDefinitionId)
    {
        // Seed via the real analysis write path so the row is a genuine Suggested/Analysis action with evidence.
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IPlaybookActionService>();
        return await service.CreateAnalysisSuggestionAsync(new PlaybookAnalysisSuggestionInput(agentDefinitionId,
            "Cite sources before answering.",
            null,
            "search",
            100,
            [Guid.NewGuid()],
            0.8d)).ConfigureAwait(false);
    }
}

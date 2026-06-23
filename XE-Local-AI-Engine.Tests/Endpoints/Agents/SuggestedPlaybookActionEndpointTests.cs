namespace XE_Local_AI_Engine.Tests.Endpoints.Agents;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class SuggestedPlaybookActionEndpointTests
{
    private static string PromoteRoute(Guid agentDefinitionId, Guid actionId)
    {
        return $"/api/local/v1/agents/{agentDefinitionId}/playbook/{actionId}/promote";
    }

    private static string RejectRoute(Guid agentDefinitionId, Guid actionId)
    {
        return $"/api/local/v1/agents/{agentDefinitionId}/playbook/{actionId}/reject";
    }

    [Test]
    public async Task Promote_WhenNoBearerToken_ReturnsUnauthorized()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, PromoteRoute(Guid.NewGuid(), Guid.NewGuid()));
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task Reject_WhenNoBearerToken_ReturnsUnauthorized()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, RejectRoute(Guid.NewGuid(), Guid.NewGuid()));
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task Promote_WhenActionMissing_ReturnsNotFound()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        var agentId = await SeedAgentAsync(factory, "Owner").ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Post, PromoteRoute(agentId, Guid.NewGuid()))
        {
            // The route carries the ids; FastEndpoints still requires a JSON body for a POST (else 415).
            Content = JsonContent.Create(new
            {
            })
        };
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Test]
    public async Task Reject_WhenActionMissing_ReturnsNotFound()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        var agentId = await SeedAgentAsync(factory, "Owner").ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Post, RejectRoute(agentId, Guid.NewGuid()))
        {
            Content = JsonContent.Create(new
            {
            })
        };
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Test]
    public async Task Promote_WhenActionBelongsToDifferentAgent_ReturnsNotFound()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        var ownerAgentId = await SeedAgentAsync(factory, "Owner").ConfigureAwait(false);
        var otherAgentId = await SeedAgentAsync(factory, "Other").ConfigureAwait(false);
        var actionId = await SeedSuggestionAsync(factory, ownerAgentId).ConfigureAwait(false);

        // Promote the owner's suggestion via the OTHER agent's route — the ownership guard must 404.
        using var request = new HttpRequestMessage(HttpMethod.Post, PromoteRoute(otherAgentId, actionId))
        {
            Content = JsonContent.Create(new
            {
            })
        };
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Test]
    public async Task Reject_WhenActionBelongsToDifferentAgent_ReturnsNotFound()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        var ownerAgentId = await SeedAgentAsync(factory, "Owner").ConfigureAwait(false);
        var otherAgentId = await SeedAgentAsync(factory, "Other").ConfigureAwait(false);
        var actionId = await SeedSuggestionAsync(factory, ownerAgentId).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Post, RejectRoute(otherAgentId, actionId))
        {
            Content = JsonContent.Create(new
            {
            })
        };
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Test]
    public async Task Promote_WhenOwnedPendingSuggestionWithoutEval_ReturnsConflictEvalRequired()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        var agentId = await SeedAgentAsync(factory, "Owner").ConfigureAwait(false);
        var actionId = await SeedSuggestionAsync(factory, agentId).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Post, PromoteRoute(agentId, actionId))
        {
            Content = JsonContent.Create(new
            {
            })
        };
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        // The eval gate blocks a promote until the latest eval has passed. A freshly-authored suggestion
        // has no eval, so the gate returns 409 EvalRequired and the action stays Suggested (still inert). A successful
        // 200/Enabled promote requires a passing eval (model-dependent — covered by the Wave-2 service unit tests).
        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        AssertEx.Equal("EvalRequired", root.GetProperty("status").GetString());
    }

    [Test]
    public async Task Reject_BodyLessPost_IsAcceptedNot415()
    {
        // Regression for the live 415 ("convert playbook → empty notification"): this route-only POST binds the agent
        // and action ids from the route, so the hey-api client sends no body — and therefore no Content-Type. The
        // endpoint must accept that instead of answering 415 Unsupported Media Type. A seeded pending suggestion is
        // archived (200), which proves the request was bound and dispatched rather than rejected at the media-type gate.
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        var agentId = await SeedAgentAsync(factory, "Owner").ConfigureAwait(false);
        var actionId = await SeedSuggestionAsync(factory, agentId).ConfigureAwait(false);

        // No HttpContent at all → the request carries no Content-Type header (the exact shape of a body-less fetch).
        using var request = new HttpRequestMessage(HttpMethod.Post, RejectRoute(agentId, actionId));
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.NotEqual(HttpStatusCode.UnsupportedMediaType, response.StatusCode, "Body-less reject POST must not return 415.");
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var document = JsonDocument.Parse(payload);
        AssertEx.Equal("Archived", document.RootElement.GetProperty("state").GetString());
    }

    [Test]
    public async Task Reject_WhenOwnedPendingSuggestion_ReturnsOkWithArchivedState()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        var agentId = await SeedAgentAsync(factory, "Owner").ConfigureAwait(false);
        var actionId = await SeedSuggestionAsync(factory, agentId).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Post, RejectRoute(agentId, actionId))
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

        // Reject archives the suggestion (provenance preserved rather than hard-deleted).
        AssertEx.Equal("Archived", root.GetProperty("state").GetString());
        AssertEx.Equal("Analysis", root.GetProperty("source").GetString());
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

    private static async Task<Guid> SeedSuggestionAsync(TestingWebAppFactory factory, Guid agentDefinitionId)
    {
        // Seed via the real analysis write path so the row is a genuine Suggested/Analysis action with evidence.
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IPlaybookActionService>();
        var created = await service.CreateAnalysisSuggestionAsync(new PlaybookAnalysisSuggestionInput(agentDefinitionId,
            "Cite sources before answering.",
            TriggerCondition: null,
            "search",
            Priority: 100,
            [Guid.NewGuid()],
            Confidence: 0.8d)).ConfigureAwait(false);
        return created.Id;
    }
}

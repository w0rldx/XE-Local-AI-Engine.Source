namespace XE_Local_AI_Engine.Tests.Endpoints.Agents;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Playbook eval endpoint (<c>POST agents/{id}/playbook/{actionId}/eval</c>). The happy path is exercised against
///     a Suggested action with NO golden cases — the eval service short-circuits to a failing result without calling the
///     model, so the full endpoint → service → persist path runs with no Ollama. The model-dependent scoring path is
///     covered by the Wave-2 service unit tests.
/// </summary>
public sealed class RunPlaybookActionEvalEndpointTests
{
    private static string EvalRoute(Guid agentDefinitionId, Guid actionId)
    {
        return $"/api/local/v1/agents/{agentDefinitionId}/playbook/{actionId}/eval";
    }

    [Test]
    public async Task RunEval_WhenNoBearerToken_ReturnsUnauthorized()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, EvalRoute(Guid.NewGuid(), Guid.NewGuid()))
        {
            Content = JsonContent.Create(new
            {
            })
        };
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task RunEval_BodyLessPost_IsAcceptedNot415()
    {
        // Regression for the live 415 ("convert playbook → empty notification"): this route-only POST binds the agent
        // and action ids from the route, so the hey-api client sends no body — and therefore no Content-Type. The
        // endpoint must accept that instead of answering 415 Unsupported Media Type. A seeded suggestion with no golden
        // cases yields 200 with a failing eval result, which proves the request was bound and dispatched rather than
        // rejected at the media-type gate.
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        var agentId = await SeedAgentAsync(factory, "Owner").ConfigureAwait(false);
        var actionId = await SeedSuggestionAsync(factory, agentId).ConfigureAwait(false);

        // No HttpContent at all → the request carries no Content-Type header (the exact shape of a body-less fetch).
        using var request = new HttpRequestMessage(HttpMethod.Post, EvalRoute(agentId, actionId));
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.NotEqual(HttpStatusCode.UnsupportedMediaType, response.StatusCode, "Body-less eval POST must not return 415.");
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Test]
    public async Task RunEval_WhenActionMissing_ReturnsNotFound()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        var agentId = await SeedAgentAsync(factory, "Owner").ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Post, EvalRoute(agentId, Guid.NewGuid()))
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
    public async Task RunEval_WhenActionBelongsToDifferentAgent_ReturnsNotFound()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        var ownerAgentId = await SeedAgentAsync(factory, "Owner").ConfigureAwait(false);
        var otherAgentId = await SeedAgentAsync(factory, "Other").ConfigureAwait(false);
        var actionId = await SeedSuggestionAsync(factory, ownerAgentId).ConfigureAwait(false);

        // Run eval on the owner's suggestion via the OTHER agent's route — the ownership guard must 404.
        using var request = new HttpRequestMessage(HttpMethod.Post, EvalRoute(otherAgentId, actionId))
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
    public async Task RunEval_WhenActionEnabledNotPending_ReturnsNotFound()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        var agentId = await SeedAgentAsync(factory, "Owner").ConfigureAwait(false);

        // A manual Enabled action is not a pending Suggested/Analysis action — the eval guard 404s.
        Guid enabledActionId;
        using (var scope = factory.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IPlaybookActionService>();
            var created = await service.CreateAsync(new PlaybookActionInput(agentId,
                PlaybookActionState.Enabled,
                PlaybookActionSource.Manual,
                TriggerCondition: null,
                "Always cite sources.",
                Scope: null,
                Priority: 10)).ConfigureAwait(false);
            enabledActionId = created.Id;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, EvalRoute(agentId, enabledActionId))
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
    public async Task RunEval_WhenNoGoldenCases_ReturnsOkWithFailingEvalResult()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        var agentId = await SeedAgentAsync(factory, "Owner").ConfigureAwait(false);
        var actionId = await SeedSuggestionAsync(factory, agentId).ConfigureAwait(false);

        // No golden cases seeded: the eval service records a failing result (no-regression unprovable with zero cases)
        // WITHOUT calling the model, so the full endpoint path runs with no Ollama dependency.
        using var request = new HttpRequestMessage(HttpMethod.Post, EvalRoute(agentId, actionId))
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

        // The action is still a pending Suggested/Analysis action; it now carries a failing eval result.
        AssertEx.Equal("Suggested", root.GetProperty("state").GetString());
        var evalResult = root.GetProperty("evalResult");
        AssertEx.False(evalResult.GetProperty("passed").GetBoolean(), "An empty golden set can never prove no-regression.");
        AssertEx.Equal(0, evalResult.GetProperty("goldenCaseCount").GetInt32());
        AssertEx.Equal(0, evalResult.GetProperty("cases").GetArrayLength());
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

    private static async Task<Guid> SeedSuggestionAsync(TestingWebAppFactory factory, Guid agentDefinitionId)
    {
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IPlaybookActionService>();
        var created = await service.CreateAnalysisSuggestionAsync(new PlaybookAnalysisSuggestionInput(agentDefinitionId,
            "Cite sources before answering.",
            TriggerCondition: null,
            Scope: "search",
            Priority: 100,
            SourceFeedbackIds: [Guid.NewGuid()],
            Confidence: 0.8d)).ConfigureAwait(false);
        return created.Id;
    }
}

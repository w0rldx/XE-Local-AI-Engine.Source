namespace XE_Local_AI_Engine.Tests.Endpoints.Agents;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Eval;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Promote endpoint tests for the eval gate and enabled-action cap. A Suggested action with no recorded eval
///     cannot be promoted — the gate returns 409 with a typed conflict body (<c>{ status: "EvalRequired", reason }</c>)
///     and the action stays Suggested (still inert). When the agent is already at the cap, a promote whose eval passed is
///     blocked with 409 (<c>{ status: "CapReached", reason }</c>).
/// </summary>
public sealed class PromoteSuggestedPlaybookActionGateEndpointTests
{
    private static string PromoteRoute(Guid agentDefinitionId, Guid actionId)
    {
        return $"/api/local/v1/agents/{agentDefinitionId}/playbook/{actionId}/promote";
    }

    [Test]
    public async Task Promote_WhenNoBearerToken_ReturnsUnauthorized()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, PromoteRoute(Guid.NewGuid(), Guid.NewGuid()))
        {
            Content = JsonContent.Create(new
            {
            })
        };
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task Promote_BodyLessPost_IsAcceptedNot415()
    {
        // Regression for the live 415 ("convert playbook → empty notification"): this route-only POST binds the agent
        // and action ids from the route, so the hey-api client sends no body — and therefore no Content-Type. The
        // endpoint must accept that instead of answering 415 Unsupported Media Type. A seeded suggestion with no eval
        // yields 409 EvalRequired, which proves the request was bound and dispatched rather than rejected at the
        // media-type gate.
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        var agentId = await SeedAgentAsync(factory, "Owner").ConfigureAwait(false);
        var actionId = await SeedSuggestionAsync(factory, agentId).ConfigureAwait(false);

        // No HttpContent at all → the request carries no Content-Type header (the exact shape of a body-less fetch).
        using var request = new HttpRequestMessage(HttpMethod.Post, PromoteRoute(agentId, actionId));
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.NotEqual(HttpStatusCode.UnsupportedMediaType, response.StatusCode, "Body-less promote POST must not return 415.");
        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var document = JsonDocument.Parse(payload);
        AssertEx.Equal("EvalRequired", document.RootElement.GetProperty("status").GetString());
    }

    [Test]
    public async Task Promote_WhenSuggestionHasNoEvalResult_ReturnsConflictEvalRequired()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        var agentId = await SeedAgentAsync(factory, "Owner").ConfigureAwait(false);
        var actionId = await SeedSuggestionAsync(factory, agentId).ConfigureAwait(false);

        // No eval has run since the suggestion was authored → the gate blocks the promote with EvalRequired.
        using var request = new HttpRequestMessage(HttpMethod.Post, PromoteRoute(agentId, actionId))
        {
            Content = JsonContent.Create(new
            {
            })
        };
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        AssertEx.Equal("EvalRequired", root.GetProperty("status").GetString());
        AssertEx.False(string.IsNullOrWhiteSpace(root.GetProperty("reason").GetString()), "The conflict carries a human reason for the panel.");

        // The blocked promote leaves the action Suggested (still inert).
        using var verifyScope = factory.Services.CreateScope();
        var service = verifyScope.ServiceProvider.GetRequiredService<IPlaybookActionService>();
        var stored = AssertEx.NotNull(await service.GetByIdAsync(actionId).ConfigureAwait(false));
        AssertEx.Equal(PlaybookActionState.Suggested, stored.State);
    }

    [Test]
    public async Task Promote_WhenAgentAtEnabledCap_ReturnsConflictCapReached()
    {
        // Cap of 1 (the floor the PostConfigure clamps to) with one already-Enabled action puts the agent at the cap, so a
        // promote whose eval passed and is current is blocked by the enabled-action hard cap rather than the eval gate.
        await using var factory = new TestingWebAppFactory
        {
            ConfigureAdditionalTestServices = static services =>
                services.Configure<PlaybookActionOptions>(static options => options.MaxEnabledActions = 1)
        };
        using var client = factory.CreateClient();

        var agentId = await SeedAgentAsync(factory, "Owner").ConfigureAwait(false);
        await SeedEnabledActionAsync(factory, agentId).ConfigureAwait(false);
        var actionId = await SeedSuggestionAsync(factory, agentId).ConfigureAwait(false);
        await RecordPassingEvalAsync(factory, agentId, actionId).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Post, PromoteRoute(agentId, actionId))
        {
            Content = JsonContent.Create(new
            {
            })
        };
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        AssertEx.Equal("CapReached", root.GetProperty("status").GetString());
        AssertEx.False(string.IsNullOrWhiteSpace(root.GetProperty("reason").GetString()), "The conflict carries a human reason for the panel.");

        // The blocked promote leaves the action Suggested (still inert).
        using var verifyScope = factory.Services.CreateScope();
        var service = verifyScope.ServiceProvider.GetRequiredService<IPlaybookActionService>();
        var stored = AssertEx.NotNull(await service.GetByIdAsync(actionId).ConfigureAwait(false));
        AssertEx.Equal(PlaybookActionState.Suggested, stored.State);
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

    private static async Task SeedEnabledActionAsync(TestingWebAppFactory factory, Guid agentDefinitionId)
    {
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IPlaybookActionService>();
        _ = await service.CreateAsync(new PlaybookActionInput(agentDefinitionId,
            PlaybookActionState.Enabled,
            PlaybookActionSource.Manual,
            TriggerCondition: null,
            "Always cite the tool you used.",
            Scope: null,
            Priority: 50)).ConfigureAwait(false);
    }

    private static async Task RecordPassingEvalAsync(TestingWebAppFactory factory, Guid agentDefinitionId, Guid actionId)
    {
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IPlaybookActionService>();
        var current = AssertEx.NotNull(await service.GetByIdAsync(actionId).ConfigureAwait(false));

        // A passing eval pinned to the action's current Version so the eval gate lets the promote through to the cap check.
        var eval = new PlaybookEvalResult(Passed: true,
            EvaluatedAtUtc: 1_000,
            ActionVersionAtEval: current.Version,
            ModelName: "test-model",
            GoldenCaseCount: 1,
            GoldenCaseTotal: 1,
            BaselinePassCount: 1,
            CandidatePassCount: 1,
            RegressedCaseCount: 0,
            ImprovedCaseCount: 0,
            Cases: []);
        var json = JsonSerializer.Serialize(eval, PlaybookEvalResult.SerializerOptions);
        _ = AssertEx.NotNull(await service.RecordEvalResultAsync(agentDefinitionId, actionId, json).ConfigureAwait(false));
    }
}

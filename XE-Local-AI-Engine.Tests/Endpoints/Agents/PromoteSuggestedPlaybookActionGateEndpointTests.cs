namespace XE_Local_AI_Engine.Tests.Endpoints.Agents;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Playbook P4: the promote endpoint's eval gate. A Suggested action with no recorded eval cannot be promoted — the
///     gate returns 409 with a typed conflict body (<c>{ status: "EvalRequired", reason }</c>) and the action stays
///     Suggested (still inert).
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
            Content = JsonContent.Create(new { })
        };
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
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
            Content = JsonContent.Create(new { })
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

    private static async Task<Guid> SeedSuggestionAsync(TestingWebAppFactory factory, Guid agentDefinitionId)
    {
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IPlaybookActionService>();
        var created = await service.CreateAnalysisSuggestionAsync(new PlaybookAnalysisSuggestionInput(
            agentDefinitionId,
            "Cite sources before answering.",
            TriggerCondition: null,
            Scope: "search",
            Priority: 100,
            SourceFeedbackIds: [Guid.NewGuid()],
            Confidence: 0.8d)).ConfigureAwait(false);
        return created.Id;
    }
}

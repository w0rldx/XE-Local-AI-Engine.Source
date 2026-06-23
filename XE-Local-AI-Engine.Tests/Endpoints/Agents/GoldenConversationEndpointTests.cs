namespace XE_Local_AI_Engine.Tests.Endpoints.Agents;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Golden conversation CRUD endpoints (<c>agents/{id}/golden-conversations</c>). Operator-gated; create
///     round-trips the typed input turns + assertion through the encrypted store; list returns the <c>{ items }</c>
///     wrapper; delete is ownership-guarded (cross-agent → 404).
/// </summary>
public sealed class GoldenConversationEndpointTests
{
    private static string ListRoute(Guid agentDefinitionId)
    {
        return $"/api/local/v1/agents/{agentDefinitionId}/golden-conversations";
    }

    private static string ItemRoute(Guid agentDefinitionId, Guid goldenId)
    {
        return $"/api/local/v1/agents/{agentDefinitionId}/golden-conversations/{goldenId}";
    }

    private static object BuildCreateBody()
    {
        return new
        {
            title = "Cites a source",
            inputTurns = new[]
            {
                new
                {
                    role = "user",
                    text = "What is the capital of France?"
                }
            },
            assertion = new
            {
                requiredPhrases = new[]
                {
                    "Paris"
                },
                forbiddenPhrases = new[]
                {
                    "London"
                }
            },
            rubric = (string?)null,
            enabled = true
        };
    }

    [Test]
    public async Task List_WhenNoBearerToken_ReturnsUnauthorized()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, ListRoute(Guid.NewGuid()));
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task Create_WhenNoBearerToken_ReturnsUnauthorized()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, ListRoute(Guid.NewGuid()))
        {
            Content = JsonContent.Create(BuildCreateBody())
        };
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task Delete_WhenNoBearerToken_ReturnsUnauthorized()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Delete, ItemRoute(Guid.NewGuid(), Guid.NewGuid()));
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task Create_WhenValid_ReturnsOkAndRoundTripsInputTurnsAndAssertion()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        var agentId = await SeedAgentAsync(factory, "Owner").ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Post, ListRoute(agentId))
        {
            Content = JsonContent.Create(BuildCreateBody())
        };
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        AssertEx.Equal("Cites a source", root.GetProperty("title").GetString());
        AssertEx.True(root.GetProperty("enabled").GetBoolean(), "Enabled defaults to true.");

        var turns = root.GetProperty("inputTurns");
        AssertEx.Equal(expected: 1, turns.GetArrayLength());
        AssertEx.Equal("user", turns[0].GetProperty("role").GetString());
        AssertEx.Equal("What is the capital of France?", turns[0].GetProperty("text").GetString());

        var assertion = root.GetProperty("assertion");
        AssertEx.Equal("Paris", assertion.GetProperty("requiredPhrases")[0].GetString());
        AssertEx.Equal("London", assertion.GetProperty("forbiddenPhrases")[0].GetString());
    }

    [Test]
    public async Task Create_WhenTitleBlank_ReturnsBadRequest()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        var agentId = await SeedAgentAsync(factory, "Owner").ConfigureAwait(false);

        var body = new
        {
            title = "   ",
            inputTurns = new[]
            {
                new
                {
                    role = "user",
                    text = "Hi"
                }
            },
            assertion = new
            {
                requiredPhrases = new[]
                {
                    "Paris"
                },
                forbiddenPhrases = Array.Empty<string>()
            },
            rubric = (string?)null,
            enabled = true
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, ListRoute(agentId))
        {
            Content = JsonContent.Create(body)
        };
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Test]
    public async Task List_WhenCasesExist_ReturnsItemsWrapper()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        var agentId = await SeedAgentAsync(factory, "Owner").ConfigureAwait(false);
        await CreateGoldenAsync(factory, client, agentId).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Get, ListRoute(agentId));
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var document = JsonDocument.Parse(payload);
        var items = document.RootElement.GetProperty("items");

        AssertEx.Equal(expected: 1, items.GetArrayLength());
        AssertEx.Equal("Cites a source", items[0].GetProperty("title").GetString());
    }

    [Test]
    public async Task Delete_WhenOwnedCase_ReturnsNoContent()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        var agentId = await SeedAgentAsync(factory, "Owner").ConfigureAwait(false);
        var goldenId = await CreateGoldenAsync(factory, client, agentId).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Delete, ItemRoute(agentId, goldenId));
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Test]
    public async Task Delete_WhenCrossAgent_ReturnsNotFound()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        var ownerAgentId = await SeedAgentAsync(factory, "Owner").ConfigureAwait(false);
        var otherAgentId = await SeedAgentAsync(factory, "Other").ConfigureAwait(false);
        var goldenId = await CreateGoldenAsync(factory, client, ownerAgentId).ConfigureAwait(false);

        // Delete the owner's golden case via the OTHER agent's route — the ownership guard must 404.
        using var request = new HttpRequestMessage(HttpMethod.Delete, ItemRoute(otherAgentId, goldenId));
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task<Guid> CreateGoldenAsync(TestingWebAppFactory factory, HttpClient client, Guid agentId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ListRoute(agentId))
        {
            Content = JsonContent.Create(BuildCreateBody())
        };
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.GetProperty("id").GetGuid();
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

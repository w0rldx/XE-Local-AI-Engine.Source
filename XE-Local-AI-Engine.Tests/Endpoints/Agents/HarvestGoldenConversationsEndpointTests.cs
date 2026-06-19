namespace XE_Local_AI_Engine.Tests.Endpoints.Agents;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Harvest follow-up endpoints (<c>agents/{id}/golden-conversations/harvest</c> + <c>.../{goldenId}/approve</c>).
///     Operator-gated; the harvest POST returns the per-run counts (404 for an unknown agent); approve flips a staged
///     harvested case enabled (404 for an unknown / cross-agent / non-harvested / already-enabled case). Both are
///     route-only POSTs, so the client posts <c>{}</c> (FastEndpoints 415s a truly empty body).
/// </summary>
public sealed class HarvestGoldenConversationsEndpointTests
{
    private static string HarvestRoute(Guid agentDefinitionId)
    {
        return $"/api/local/v1/agents/{agentDefinitionId}/golden-conversations/harvest";
    }

    private static string ApproveRoute(Guid agentDefinitionId, Guid goldenId)
    {
        return $"/api/local/v1/agents/{agentDefinitionId}/golden-conversations/{goldenId}/approve";
    }

    [Test]
    public async Task Harvest_WhenNoBearerToken_ReturnsUnauthorized()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, HarvestRoute(Guid.NewGuid()))
        {
            Content = JsonContent.Create(new
            {
            })
        };
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task Harvest_WhenAgentUnknown_ReturnsNotFound()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, HarvestRoute(Guid.NewGuid()))
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
    public async Task Harvest_WhenAgentExists_ReturnsOkWithCountsBody()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        var agentId = await SeedAgentAsync(factory, "Owner").ConfigureAwait(false);

        // Route-only POST → send an empty object body (a truly empty body 415s).
        using var request = new HttpRequestMessage(HttpMethod.Post, HarvestRoute(agentId))
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

        // An agent with no thumbs-up feedback harvests nothing, but the four count fields must be present.
        AssertEx.Equal(0, root.GetProperty("thumbsUpScanned").GetInt32());
        AssertEx.Equal(0, root.GetProperty("createdCount").GetInt32());
        AssertEx.Equal(0, root.GetProperty("duplicateCount").GetInt32());
        AssertEx.Equal(0, root.GetProperty("skippedCount").GetInt32());
    }

    [Test]
    public async Task Approve_WhenNoBearerToken_ReturnsUnauthorized()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, ApproveRoute(Guid.NewGuid(), Guid.NewGuid()))
        {
            Content = JsonContent.Create(new
            {
            })
        };
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task Approve_WhenGoldenUnknown_ReturnsNotFound()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        var agentId = await SeedAgentAsync(factory, "Owner").ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Post, ApproveRoute(agentId, Guid.NewGuid()))
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
    public async Task Approve_WhenManualCase_ReturnsNotFound()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        var agentId = await SeedAgentAsync(factory, "Owner").ConfigureAwait(false);
        // A Manual (non-harvested) case must not be promotable via the approve route.
        var goldenId = await SeedGoldenAsync(factory, agentId, GoldenConversationSource.Manual, false).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Post, ApproveRoute(agentId, goldenId))
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
    public async Task Approve_WhenCrossAgent_ReturnsNotFound()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        var ownerAgentId = await SeedAgentAsync(factory, "Owner").ConfigureAwait(false);
        var otherAgentId = await SeedAgentAsync(factory, "Other").ConfigureAwait(false);
        var goldenId = await SeedGoldenAsync(factory, ownerAgentId, GoldenConversationSource.Harvested, false).ConfigureAwait(false);

        // Approve the owner's harvested case via the OTHER agent's route — the ownership guard must 404.
        using var request = new HttpRequestMessage(HttpMethod.Post, ApproveRoute(otherAgentId, goldenId))
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
    public async Task Approve_WhenHarvestedDisabledAndOwned_ReturnsOkWithEnabledTrue()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        var agentId = await SeedAgentAsync(factory, "Owner").ConfigureAwait(false);
        var goldenId = await SeedGoldenAsync(factory, agentId, GoldenConversationSource.Harvested, false).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Post, ApproveRoute(agentId, goldenId))
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

        AssertEx.Equal(goldenId, root.GetProperty("id").GetGuid());
        AssertEx.True(root.GetProperty("enabled").GetBoolean(), "Approve should enable the harvested case.");
        AssertEx.Equal("harvested", root.GetProperty("source").GetString());
    }

    private static async Task<Guid> SeedGoldenAsync(TestingWebAppFactory factory, Guid agentId, GoldenConversationSource source, bool enabled)
    {
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IGoldenConversationStore>();
        var added = await store.AddAsync(new GoldenConversationInput(agentId,
            "Seeded case",
            """[{"role":"user","text":"hi"}]""",
            null,
            "Be consistent with the approved answer.",
            enabled,
            source,
            source == GoldenConversationSource.Harvested ? Guid.NewGuid() : null,
            source == GoldenConversationSource.Harvested ? Guid.NewGuid() : null)).ConfigureAwait(false);
        return added.Id;
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
}

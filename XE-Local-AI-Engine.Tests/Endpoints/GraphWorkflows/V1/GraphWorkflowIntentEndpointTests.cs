namespace XE_Local_AI_Engine.Tests.Endpoints.GraphWorkflows.V1;

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Tests.GraphWorkflows;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <c>POST graph-workflows/intents/invoke</c> over a real store: auth, the feature gate, the 400 and 409 refusals,
///     the Started and NotMatched bodies, and a replay answering the same run.
/// </summary>
/// <remarks>Every test claims an alias of its own, because the host and its database are shared by the class.</remarks>
[Category(TestCategories.Integration)]
public sealed class GraphWorkflowIntentEndpointTests
{
    private const string Route = "/api/local/v1/graph-workflows/intents/invoke";

    [ClassDataSource<GraphWorkflowHostFixture>(Shared = SharedType.PerClass)]
    public required GraphWorkflowHostFixture Host { get; init; }

    [Test]
    public async Task Invoke_WhenTheOperatorTokenIsMissing_ReturnsUnauthorized()
    {
        using var client = Host.Factory.CreateClient();
        using var request = Request(Body("lights on"));

        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task Invoke_WithANonOperatorToken_ReturnsForbidden()
    {
        using var client = Host.Factory.CreateClient();
        using var request = Request(Body("lights on"));
        Host.Factory.AddNonOperatorBearerToken(request);

        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Test]
    public async Task Invoke_WhenTheFeatureIsDisabled_ReturnsNotFound()
    {
        await using var factory = new TestServerWebAppFactory
        {
            AdditionalConfiguration = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["GraphWorkflows:Enabled"] = "false"
            }
        };
        using var client = factory.CreateClient();
        using var request = Request(Body("lights on"));
        factory.AddNodeBearerToken(request);

        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Test]
    [Arguments("""{ "utterance": "", "requestId": "11111111-1111-1111-1111-111111111111", "utteredAt": "2026-10-15T12:00:00Z" }""")]
    [Arguments("""{ "utterance": "lights on", "requestId": "00000000-0000-0000-0000-000000000000", "utteredAt": "2026-10-15T12:00:00Z" }""")]
    [Arguments("""{ "utterance": "lights on", "requestId": "11111111-1111-1111-1111-111111111111" }""")]
    [Arguments("""{ "utterance": "lights on", "requestId": "11111111-1111-1111-1111-111111111111", "utteredAt": "yesterday" }""")]
    public async Task Invoke_WithAMalformedBody_Answers400(string body)
    {
        using var response = await SendAsync(body);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [Test]
    public async Task Invoke_WithAFutureUtterance_Answers400KeyedByUtteredAt()
    {
        using var response = await SendAsync(Body("lights on", DateTimeOffset.UtcNow.AddMinutes(5)));

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertEx.Contains(await response.Content.ReadAsStringAsync(), "utteredAt");
    }

    [Test]
    public async Task Invoke_WithAStaleUtterance_Answers409IntentExpired()
    {
        using var response = await SendAsync(Body("lights on", DateTimeOffset.UtcNow.AddHours(-1)));

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        AssertEx.Equal("GraphWorkflowIntentExpired", document.RootElement.GetProperty("conflictType").GetString());
    }

    [Test]
    public async Task Invoke_OnAnAlias_StartsTheDefinition_AndAReplayAnswersTheSameRun()
    {
        var alias = $"lights on {Guid.NewGuid():N}";
        var definitionId = await SeedIntentDefinitionAsync(alias);
        var body = Body($"  {alias.ToUpperInvariant()}! ");

        using var first = await SendAsync(body);
        using var second = await SendAsync(body);

        AssertEx.Equal(HttpStatusCode.OK, first.StatusCode, await first.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        var root = document.RootElement;
        AssertEx.Equal("Started", root.GetProperty("status").GetString());
        AssertEx.Equal("Alias", root.GetProperty("matchedBy").GetString());
        AssertEx.Equal(definitionId, root.GetProperty("definitionId").GetGuid());
        var run = root.GetProperty("run").GetProperty("run");
        AssertEx.Equal("Intent", run.GetProperty("triggerSource").GetString());
        using var replay = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        AssertEx.Equal(run.GetProperty("id").GetGuid(), replay.RootElement.GetProperty("run").GetProperty("run").GetProperty("id").GetGuid(),
            "the same request id answers the run it already started.");
    }

    [Test]
    public async Task Invoke_WhenNothingMatches_Answers200NotMatchedWithoutARun()
    {
        using var response = await SendAsync(Body($"nothing answers to {Guid.NewGuid():N}"));

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        AssertEx.Equal("NotMatched", root.GetProperty("status").GetString());
        AssertEx.Equal("None", root.GetProperty("matchedBy").GetString());
        AssertEx.Equal(JsonValueKind.Null, root.GetProperty("run").ValueKind);
        AssertEx.Contains(root.GetProperty("reason").GetString(), "no intent router is set");
    }

    private async Task<Guid> SeedIntentDefinitionAsync(string alias)
    {
        var graph = $$"""
                      { "schemaVersion": 1,
                        "nodes": [{ "key": "start", "kind": "Start", "config": { "aliases": ["{{alias}}"] } },
                                  { "key": "done", "kind": "End", "config": { "outcome": "done" } }],
                        "edges": [{ "key": "e1", "from": "start", "to": "done" }] }
                      """;
        await using var scope = Host.Factory.Services.CreateAsyncScope();
        var definitions = scope.ServiceProvider.GetRequiredService<IGraphWorkflowDefinitionService>();
        var created = await definitions.CreateAsync($"Intent {Guid.NewGuid():N}", description: null, graph);
        await definitions.UpdateAsync(created.Id, created.Version, name: null, description: null, graphJson: null,
            triggerSources: GraphWorkflowTriggerSource.Manual | GraphWorkflowTriggerSource.Intent);
        return created.Id;
    }

    private static string Body(string utterance, DateTimeOffset? utteredAt = null) =>
        JsonSerializer.Serialize(new
        {
            utterance,
            requestId = Guid.NewGuid(),
            utteredAt = utteredAt ?? DateTimeOffset.UtcNow
        });

    private async Task<HttpResponseMessage> SendAsync(string body)
    {
        using var client = Host.Factory.CreateClient();
        using var request = Request(body);
        Host.Factory.AddNodeBearerToken(request);
        return await client.SendAsync(request);
    }

    private static HttpRequestMessage Request(string body) =>
        new(HttpMethod.Post, Route)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
}

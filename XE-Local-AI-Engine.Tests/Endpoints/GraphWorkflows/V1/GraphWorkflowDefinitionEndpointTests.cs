namespace XE_Local_AI_Engine.Tests.Endpoints.GraphWorkflows.V1;

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Tests.GraphWorkflows;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>The definition half of the graph-workflow surface: the five CRUD routes plus the validate probe.</summary>
public sealed class GraphWorkflowDefinitionEndpointTests
{
    private const string Root = "/api/local/v1/graph-workflows";
    private const string Definitions = $"{Root}/definitions";
    private const string Definition = $"{Definitions}/22222222-2222-2222-2222-222222222222";
    private const string Validate = $"{Definitions}/validate";

    private static readonly Guid DefinitionId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Test]
    [Arguments("GET", Definitions)]
    [Arguments("POST", Definitions)]
    [Arguments("GET", Definition)]
    [Arguments("PUT", Definition)]
    [Arguments("DELETE", Definition)]
    [Arguments("POST", Validate)]
    public async Task Route_WhenTheOperatorTokenIsMissing_ReturnsUnauthorized(string method, string route)
    {
        await using var factory = EnabledFactory(Store());
        using var client = factory.CreateClient();
        using var request = Request(method, route);

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode, $"{method} {route} must require the operator token.");
    }

    [Test]
    [Arguments("GET", Definitions)]
    [Arguments("POST", Definitions)]
    [Arguments("GET", Definition)]
    [Arguments("PUT", Definition)]
    [Arguments("DELETE", Definition)]
    [Arguments("POST", Validate)]
    public async Task Route_WithANonOperatorToken_ReturnsForbidden(string method, string route)
    {
        await using var factory = EnabledFactory(Store());
        using var client = factory.CreateClient();
        using var request = Request(method, route);
        factory.AddNonOperatorBearerToken(request);

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Forbidden, response.StatusCode, $"{method} {route} is operator-only, so an authenticated non-operator is refused.");
    }

    [Test]
    [Arguments("GET", Definitions)]
    [Arguments("POST", Definitions)]
    [Arguments("GET", Definition)]
    [Arguments("PUT", Definition)]
    [Arguments("DELETE", Definition)]
    [Arguments("POST", Validate)]
    public async Task Route_WhenTheFeatureIsDisabled_ReturnsNotFoundWithoutReachingTheStore(string method, string route)
    {
        var store = Store();
        await using var factory = new TestServerWebAppFactory
        {
            AdditionalConfiguration = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["GraphWorkflows:Enabled"] = "false"
            },
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<IGraphWorkflowStore>();
                services.AddSingleton(store);
            }
        };

        using var response = await SendAsync(factory, method, route).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode, $"{method} {route} must answer 404 on a disabled node, never 500.");
        AssertEx.Empty(store.ReceivedCalls());
    }

    [Test]
    public async Task ListDefinitions_ProjectsTheSummaryWithoutAGraph()
    {
        var store = Store();
        store.ListDefinitionsAsync(Arg.Any<CancellationToken>()).Returns([Summary()]);
        await using var factory = EnabledFactory(store);

        using var response = await SendAsync(factory, "GET", Definitions).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        var definition = document.RootElement.GetProperty("definitions")[0];
        AssertEx.Equal("Triage", definition.GetProperty("name").GetString());
        AssertEx.Equal(3, definition.GetProperty("nodeCount").GetInt32());
        AssertEx.False(definition.TryGetProperty("graph", out _), "the list never carries a graph: it would decrypt a blob per row for nothing.");
    }

    /// <summary>
    ///     The parse is the RUNTIME's, and its node count is what gets denormalized onto the row — the number the list
    ///     reports without decrypting a graph. A count taken anywhere else is a count able to disagree with the parser.
    /// </summary>
    [Test]
    public async Task CreateDefinition_ValidatesWithTheRuntimesOwnParserAndStoresItsNodeCount()
    {
        var store = Store();
        CreateGraphWorkflowDefinitionCommand? command = null;
        store.CreateDefinitionAsync(Arg.Any<CreateGraphWorkflowDefinitionCommand>(), Arg.Any<CancellationToken>())
             .Returns(call =>
             {
                 command = call.Arg<CreateGraphWorkflowDefinitionCommand>();
                 return Snapshot(command.GraphJson, command.NodeCount);
             });
        await using var factory = EnabledFactory(store);

        using var response = await SendAsync(factory, "POST", Definitions, CreateBody(GraphWorkflowGraphs.StartAgentEnd)).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Created, response.StatusCode);
        AssertEx.NotNull(response.Headers.Location);
        var stored = AssertEx.NotNull(command);
        AssertEx.Equal(3, stored.NodeCount, "StartAgentEnd is three nodes, and the count on the row is the parser's own.");
        AssertEx.Equal("Triage", stored.Name);
        AssertEx.Equal("The one that triages.", stored.Description);
        AssertEx.Equal(1, stored.SchemaVersion);
    }

    /// <summary>
    ///     The reason the endpoint catches the validation exception itself instead of leaving it to the single-message
    ///     global handler: an author fixing a canvas gets EVERY complaint at once, each keyed to the node it belongs to.
    /// </summary>
    [Test]
    public async Task CreateDefinition_WithAGraphNothingCouldRoute_ReturnsEveryErrorAndNeverReachesTheStore()
    {
        var store = Store();
        await using var factory = EnabledFactory(store);

        using var response = await SendAsync(factory, "POST", Definitions, CreateBody(GraphWorkflowGraphs.TwoNodeConfigErrors)).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertEx.Contains(body, "reasoningEffort", StringComparison.Ordinal, $"the first node's failure must survive into the body: {body}");
        AssertEx.Contains(body, "toolName", StringComparison.Ordinal, $"and so must the second's, rather than one of the two: {body}");

        using var document = JsonDocument.Parse(body);
        var names = document.RootElement.GetProperty("errors").EnumerateArray().Select(static error => error.GetProperty("name").GetString() ?? string.Empty).ToArray();
        AssertEx.Contains(names, "a", $"each failure carries the key of the node it belongs to, so the editor can place it: {body}");
        AssertEx.Contains(names, "b", $"each failure carries the key of the node it belongs to, so the editor can place it: {body}");
        await store.DidNotReceive().CreateDefinitionAsync(Arg.Any<CreateGraphWorkflowDefinitionCommand>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     A boolean condition value must survive the round trip as a boolean. Stringified, it would compare against a
    ///     real boolean as a type mismatch, the evaluator would fail closed, and the edge would silently never fire —
    ///     with nothing in the log to say why.
    /// </summary>
    [Test]
    public async Task Definition_KeepsAConditionValuesJsonTypeThroughTheRoundTrip()
    {
        var store = Store();
        string? stored = null;
        store.CreateDefinitionAsync(Arg.Any<CreateGraphWorkflowDefinitionCommand>(), Arg.Any<CancellationToken>())
             .Returns(call =>
             {
                 stored = call.Arg<CreateGraphWorkflowDefinitionCommand>().GraphJson;
                 return Snapshot(stored);
             });
        await using var factory = EnabledFactory(store);

        using var response = await SendAsync(factory, "POST", Definitions, CreateBody(GraphWorkflowGraphs.BranchOnJson)).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Created, response.StatusCode);
        using var storedDocument = JsonDocument.Parse(AssertEx.NotNull(stored));
        AssertEx.Equal(JsonValueKind.True,
            ConditionValueKind(storedDocument.RootElement),
            "the stored graph must keep the boolean, not a string spelling of one.");
        using var document = JsonDocument.Parse(body);
        AssertEx.Equal(JsonValueKind.True, ConditionValueKind(document.RootElement.GetProperty("graph")), "and the read back must too.");
    }

    /// <summary>
    ///     A position is authoring metadata the runtime reads past, which is exactly why it has to survive: a canvas
    ///     that re-lays every node out on open has lost the layout the author drew.
    /// </summary>
    [Test]
    public async Task Definition_KeepsPositionsThroughTheRoundTrip()
    {
        var store = Store();
        string? stored = null;
        store.CreateDefinitionAsync(Arg.Any<CreateGraphWorkflowDefinitionCommand>(), Arg.Any<CancellationToken>())
             .Returns(call =>
             {
                 stored = call.Arg<CreateGraphWorkflowDefinitionCommand>().GraphJson;
                 return Snapshot(stored);
             });
        await using var factory = EnabledFactory(store);

        using var response = await SendAsync(factory, "POST", Definitions, CreateBody(GraphWorkflowGraphs.ToolNode)).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Created, response.StatusCode);
        using var storedDocument = JsonDocument.Parse(AssertEx.NotNull(stored));
        AssertPosition(storedDocument.RootElement, "the stored graph");
        using var document = JsonDocument.Parse(body);
        AssertPosition(document.RootElement.GetProperty("graph"), "the response");
        AssertEx.False(NodeByKey(document.RootElement.GetProperty("graph"), "peek").TryGetProperty("position", out var absent) && absent.ValueKind != JsonValueKind.Null,
            "and a node that was drawn nowhere still reads as nowhere, so the client lays it out rather than stacking it on the origin.");
    }

    [Test]
    public async Task UpdateDefinition_WithoutAGraph_LeavesTheStoredOneAlone()
    {
        var store = Store();
        store.UpdateDefinitionAsync(Arg.Any<UpdateGraphWorkflowDefinitionCommand>(), Arg.Any<CancellationToken>()).Returns(Snapshot());
        await using var factory = EnabledFactory(store);

        using var response = await SendAsync(factory, "PUT", Definition, """{"version":4,"name":"renamed","description":"still triage"}""").ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        await store.Received(1)
                   .UpdateDefinitionAsync(Arg.Is<UpdateGraphWorkflowDefinitionCommand>(command => command.DefinitionId == DefinitionId
                                                                                                  && command.ExpectedVersion == 4
                                                                                                  && command.Name == "renamed"
                                                                                                  && command.Description == "still triage"
                                                                                                  && command.GraphJson == null
                                                                                                  && command.NodeCount == null),
                       Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateDefinition_WhenTheVersionIsStale_Returns409WithTheDefinitionConflictType()
    {
        var store = Store();
        store.UpdateDefinitionAsync(Arg.Any<UpdateGraphWorkflowDefinitionCommand>(), Arg.Any<CancellationToken>())
             .ThrowsAsyncForAnyArgs(new GraphWorkflowDefinitionConflictException("The definition version is stale."));
        await using var factory = EnabledFactory(store);

        using var response = await SendAsync(factory, "PUT", Definition, """{"version":1,"name":"renamed"}""").ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        AssertEx.Equal("GraphWorkflowDefinitionConflict",
            document.RootElement.GetProperty("conflictType").GetString(),
            "the conflict type is what proves the handler arm is wired rather than only the status code.");
    }

    [Test]
    public async Task DeleteDefinition_WhileARunPinsIt_Returns409WithTheDefinitionConflictType()
    {
        var store = Store();
        store.DeleteDefinitionAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
             .ThrowsAsyncForAnyArgs(new GraphWorkflowDefinitionConflictException("A live run still pins the definition."));
        await using var factory = EnabledFactory(store);

        using var response = await SendAsync(factory, "DELETE", Definition).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        AssertEx.Equal("GraphWorkflowDefinitionConflict", document.RootElement.GetProperty("conflictType").GetString());
    }

    [Test]
    public async Task DeleteDefinition_WhenNothingPinsIt_RemovesAndAnswersNoContent()
    {
        var store = Store();
        await using var factory = EnabledFactory(store);

        using var response = await SendAsync(factory, "DELETE", Definition).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await store.Received(1).DeleteDefinitionAsync(DefinitionId, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetDefinition_WhenTheStoreHasNoSuchRow_ReturnsNotFound()
    {
        var store = Store();
        store.GetDefinitionAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
             .ThrowsAsyncForAnyArgs(new GraphWorkflowNotFoundException("Graph workflow definition was not found."));
        await using var factory = EnabledFactory(store);

        using var response = await SendAsync(factory, "GET", Definition).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode, "a missing definition is a 404, never the catch-all 500.");
    }

    private static JsonValueKind ConditionValueKind(JsonElement graph) =>
        graph.GetProperty("edges")
             .EnumerateArray()
             .First(static edge => edge.GetProperty("key").GetString() == "e3")
             .GetProperty("condition")
             .GetProperty("value")
             .ValueKind;

    private static JsonElement NodeByKey(JsonElement graph, string key) =>
        graph.GetProperty("nodes").EnumerateArray().First(node => node.GetProperty("key").GetString() == key);

    private static void AssertPosition(JsonElement graph, string where)
    {
        var position = NodeByKey(graph, "lookup").GetProperty("position");
        AssertEx.Equal(12d, position.GetProperty("x").GetDouble(), $"{where} must keep the node's x.");
        AssertEx.Equal(-4d, position.GetProperty("y").GetDouble(), $"{where} must keep the node's y.");
    }

    private static string CreateBody(string graph) =>
        $$"""{"name":"Triage","description":"The one that triages.","graph":{{graph}}}""";

    private static IGraphWorkflowStore Store()
    {
        var store = Substitute.For<IGraphWorkflowStore>();
        store.ListDefinitionsAsync(Arg.Any<CancellationToken>()).Returns([]);
        store.GetDefinitionAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Snapshot());
        store.CreateDefinitionAsync(Arg.Any<CreateGraphWorkflowDefinitionCommand>(), Arg.Any<CancellationToken>()).Returns(Snapshot());
        store.UpdateDefinitionAsync(Arg.Any<UpdateGraphWorkflowDefinitionCommand>(), Arg.Any<CancellationToken>()).Returns(Snapshot());
        return store;
    }

    private static GraphWorkflowDefinitionSnapshot Snapshot(string? graphJson = null, int nodeCount = 3) =>
        new(DefinitionId,
            "Triage",
            "The one that triages.",
            graphJson ?? GraphWorkflowGraphs.StartAgentEnd,
            "graph-hash",
            nodeCount,
            SchemaVersion: 1,
            Version: 4,
            CreatedAtUtc: 1,
            UpdatedAtUtc: 2);

    private static GraphWorkflowDefinitionSummary Summary() =>
        new(DefinitionId, "Triage", "The one that triages.", "graph-hash", NodeCount: 3, SchemaVersion: 1, Version: 4, CreatedAtUtc: 1, UpdatedAtUtc: 2);

    private static async Task<HttpResponseMessage> SendAsync(TestServerWebAppFactory factory, string method, string route, string? body = null)
    {
        using var client = factory.CreateClient();
        using var request = Request(method, route, body);
        factory.AddNodeBearerToken(request);
        return await client.SendAsync(request).ConfigureAwait(false);
    }

    private static HttpRequestMessage Request(string method, string route, string? body = null)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), route);
        if (method is "POST" or "PATCH" or "PUT")
        {
            request.Content = new StringContent(body ?? "{}", Encoding.UTF8, "application/json");
        }

        return request;
    }

    private static TestServerWebAppFactory EnabledFactory(IGraphWorkflowStore store) =>
        new()
        {
            AdditionalConfiguration = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["GraphWorkflows:Enabled"] = "true"
            },
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<IGraphWorkflowStore>();
                services.AddSingleton(store);
            }
        };
}

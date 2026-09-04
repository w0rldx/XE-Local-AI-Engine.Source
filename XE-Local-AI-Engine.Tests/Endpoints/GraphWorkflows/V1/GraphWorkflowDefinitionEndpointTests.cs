namespace XE_Local_AI_Engine.Tests.Endpoints.GraphWorkflows.V1;

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1;
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

        // config is raw JSON on the way through, and a response schema is the member most able to be flattened by a
        // mapper that thought it understood the shape. An Agent that loses it stops answering in JSON at all.
        var schema = NodeByKey(storedDocument.RootElement, "analyze").GetProperty("config").GetProperty("responseJsonSchema");
        AssertEx.Equal("object", schema.GetProperty("type").GetString(), "the stored config must keep the response schema verbatim.");
        AssertEx.Equal("boolean",
            schema.GetProperty("properties").GetProperty("requiresReview").GetProperty("type").GetString(),
            "including the nested property the condition edges branch on.");
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

        // The Tool node's own config members, which no other kind carries: a mapper that projected a common subset
        // would drop exactly these, and the tool would run with no arguments and nothing bound to its inputs.
        var toolConfig = NodeByKey(storedDocument.RootElement, "lookup").GetProperty("config");
        AssertEx.Equal("notes.md", toolConfig.GetProperty("arguments").GetProperty("path").GetString(), "the stored config must keep the literal arguments.");
        AssertEx.Equal("output.json.path",
            toolConfig.GetProperty("argumentBindings").GetProperty("path").GetString(),
            "and the bindings that overwrite them from an upstream output.");
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

    /// <summary>
    ///     An explicit <c>null</c> is a condition value the evaluator compares — <c>Compare</c> has a null arm — and a
    ///     missing member is not one at all. A round trip that collapses the two turns a working equality into the one
    ///     shape the parser refuses, so the save that follows the edit answers 400 for a graph the author never
    ///     changed.
    /// </summary>
    [Test]
    public async Task Definition_KeepsAnExplicitNullConditionValueThroughTheRoundTrip()
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

        using var response = await SendAsync(factory, "POST", Definitions, CreateBody(GraphWorkflowGraphs.ConditionOnExplicitNull)).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Created, response.StatusCode);
        using var storedDocument = JsonDocument.Parse(AssertEx.NotNull(stored));
        AssertExplicitNullCondition(storedDocument.RootElement, "the stored graph");
        using var document = JsonDocument.Parse(body);
        AssertExplicitNullCondition(document.RootElement.GetProperty("graph"), "the response");
    }

    /// <summary>
    ///     The count is denormalized onto the row so the list never decrypts a blob, so it has to arrive WITH the graph
    ///     it was taken from. A graph reaching the store beside the previous graph's count is the one lie that column
    ///     exists to prevent.
    /// </summary>
    [Test]
    public async Task UpdateDefinition_WithAGraph_SendsItAndItsNodeCountTogether()
    {
        var store = Store();
        await using var factory = EnabledFactory(store);

        using var response = await SendAsync(factory,
                                       "PUT",
                                       Definition,
                                       $$"""{"version":4,"name":"renamed","graph":{{GraphWorkflowGraphs.StartAgentEnd}}}""")
                                   .ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        await store.Received(1)
                   .UpdateDefinitionAsync(Arg.Is<UpdateGraphWorkflowDefinitionCommand>(command => command.GraphJson != null && command.NodeCount == 3),
                       Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     Null and empty are different edits: null leaves the stored description alone, an empty string clears it. An
    ///     author who deleted the text has no other way to say so.
    /// </summary>
    [Test]
    public async Task UpdateDefinition_WithAnEmptyDescription_ClearsIt()
    {
        var store = Store();
        await using var factory = EnabledFactory(store);

        using var response = await SendAsync(factory, "PUT", Definition, """{"version":4,"description":""}""").ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        await store.Received(1)
                   .UpdateDefinitionAsync(Arg.Is<UpdateGraphWorkflowDefinitionCommand>(command => command.Description == string.Empty),
                       Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     The graph-carrying routes cap their body. Without one they inherit the host's 30 MB default, and a body that
    ///     size is bound, parsed by the runtime's parser and hashed before the node cap could refuse it.
    /// </summary>
    [Test]
    [Arguments("POST", Definitions)]
    [Arguments("PUT", Definition)]
    [Arguments("POST", Validate)]
    public async Task GraphRoute_WithABodyOverTheCap_Returns413AndNeverReachesTheStore(string method, string route)
    {
        var store = Store();
        await using var factory = EnabledFactory(store);

        using var response = await SendAsync(factory, method, route, OversizedBody()).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode, $"{method} {route} must refuse a body over the cap.");
        AssertEx.Empty(store.ReceivedCalls());
    }

    /// <summary>
    ///     A body whose bulk is in the GRAPH rather than in a bounded field: name and description have their own length
    ///     rules, so an oversized one of those would answer 400 from the validator and prove nothing about the cap.
    /// </summary>
    private static string OversizedBody()
    {
        var graph = GraphWorkflowGraphs.StartAgentEnd.Replace("Analyze the input.", new string('a', (int)GraphWorkflowRequestSizeLimit.MaxBytes), StringComparison.Ordinal);
        return $$"""{"definitionId":"22222222-2222-2222-2222-222222222222","version":4,"name":"Triage","graph":{{graph}}}""";
    }

    private static void AssertExplicitNullCondition(JsonElement graph, string where)
    {
        AssertEx.Equal(JsonValueKind.Null,
            EdgeByKey(graph, "e2").GetProperty("condition").GetProperty("value").ValueKind,
            $"{where} must keep the explicit null as a null the evaluator can compare, not as a missing member.");
        AssertEx.False(EdgeByKey(graph, "e3").GetProperty("condition").TryGetProperty("value", out _),
            $"{where} must leave an operator that takes no value carrying none, rather than inventing a null for it.");
    }

    private static JsonElement EdgeByKey(JsonElement graph, string key) =>
        graph.GetProperty("edges").EnumerateArray().First(edge => edge.GetProperty("key").GetString() == key);

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

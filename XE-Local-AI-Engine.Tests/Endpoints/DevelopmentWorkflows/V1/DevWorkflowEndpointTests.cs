namespace XE_Local_AI_Engine.Tests.Endpoints.DevelopmentWorkflows.V1;

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>The work-item and definition halves of the surface. Runs, feeds and the decision live in their own file.</summary>
public sealed class DevWorkflowEndpointTests
{
    private const string Root = "/api/local/v1/development-workflows";
    private const string WorkItems = $"{Root}/work-items";
    private const string WorkItem = $"{WorkItems}/11111111-1111-1111-1111-111111111111";
    private const string Definitions = $"{Root}/definitions";
    private const string Definition = $"{Definitions}/22222222-2222-2222-2222-222222222222";
    private const string RuleSets = $"{Root}/rule-sets";
    private const string RuleSet = $"{RuleSets}/44444444-4444-4444-4444-444444444444";

    private static readonly Guid WorkItemId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid DefinitionId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid RunId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid RuleSetId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    /// <summary>The graph the seeded Slice-A template has: one agent into one terminal gate.</summary>
    private const string SampleGraph = """
                                       {"schemaVersion":1,
                                        "nodes":[{"nodeKey":"research","nodeType":"Agent","label":"Research","agentSeedSlug":"researcher","maxAttempts":3},
                                                 {"nodeKey":"approval","nodeType":"HumanGate","label":"Approve the plan","instructions":"Read the plan."}],
                                        "edges":[{"from":"research","to":"approval"}]}
                                       """;

    [Test]
    [Arguments("GET", WorkItems)]
    [Arguments("POST", WorkItems)]
    [Arguments("GET", WorkItem)]
    [Arguments("PATCH", WorkItem)]
    [Arguments("DELETE", WorkItem)]
    [Arguments("GET", Definitions)]
    [Arguments("POST", Definitions)]
    [Arguments("GET", Definition)]
    [Arguments("PUT", Definition)]
    [Arguments("DELETE", Definition)]
    [Arguments("GET", RuleSets)]
    [Arguments("POST", RuleSets)]
    [Arguments("GET", RuleSet)]
    [Arguments("PUT", RuleSet)]
    [Arguments("DELETE", RuleSet)]
    public async Task DevWorkflowRoute_WhenTheOperatorTokenIsMissing_ReturnsUnauthorized(string method, string route)
    {
        await using var factory = EnabledFactory(Store(), Substitute.For<IDevWorkflowRunService>());
        using var client = factory.CreateClient();
        using var request = Request(method, route);

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode, $"{method} {route} must require the operator token.");
    }

    [Test]
    [Arguments("GET", WorkItems)]
    [Arguments("POST", WorkItems)]
    [Arguments("GET", WorkItem)]
    [Arguments("GET", Definitions)]
    public async Task DevWorkflowRoute_WhenTheFeatureIsDisabled_ReturnsNotFoundWithoutReachingTheStore(string method, string route)
    {
        var store = Store();
        await using var factory = new TestServerWebAppFactory
        {
            AdditionalConfiguration = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["DevWorkflows:Enabled"] = "false"
            },
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<IDevWorkflowStore>();
                services.AddSingleton(store);
            }
        };

        using var response = await SendAsync(factory, method, route, method == "POST" ? "{}" : null).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode, $"{method} {route} must answer 404 on a disabled node, never 500.");
        AssertEx.Empty(store.ReceivedCalls());
    }

    [Test]
    public async Task ListWorkItems_ProjectsTheLatestRunAndItsCounters()
    {
        var store = Store();
        store.ListWorkItemsAsync(null, Arg.Any<CancellationToken>()).Returns([WorkItemSnapshot()]);
        await using var factory = EnabledFactory(store);

        using var response = await SendAsync(factory, "GET", WorkItems).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        var item = document.RootElement.GetProperty("items")[0];
        AssertEx.Equal("Active", item.GetProperty("status").GetString());
        AssertEx.Equal("WaitingForApproval", item.GetProperty("latestRunStatus").GetString());
        AssertEx.Equal("Research → Plan → Approval", item.GetProperty("definitionName").GetString());
        AssertEx.Equal(2, item.GetProperty("totalNodeCount").GetInt32());
        AssertEx.Equal(1, item.GetProperty("runningNodeCount").GetInt32());
    }

    [Test]
    public async Task ListWorkItems_ForwardsTheStatusFilter()
    {
        var store = Store();
        store.ListWorkItemsAsync(Arg.Any<DevWorkflowWorkItemStatus?>(), Arg.Any<CancellationToken>()).Returns([]);
        await using var factory = EnabledFactory(store);

        using var response = await SendAsync(factory, "GET", $"{WorkItems}?status=Blocked").ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        await store.Received(1).ListWorkItemsAsync(DevWorkflowWorkItemStatus.Blocked, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ListWorkItems_WithAStatusThatIsNotAMember_ReturnsBadRequestAndNeverReachesTheStore()
    {
        var store = Store();
        await using var factory = EnabledFactory(store);

        using var response = await SendAsync(factory, "GET", $"{WorkItems}?status=Nonsense").ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await store.DidNotReceive().ListWorkItemsAsync(Arg.Any<DevWorkflowWorkItemStatus?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CreateWorkItem_WhenValid_ReturnsCreatedWithLocationAndForwardsTheRequestText()
    {
        var store = Store();
        store.CreateWorkItemAsync(Arg.Any<CreateDevWorkflowWorkItemCommand>(), Arg.Any<CancellationToken>()).Returns(WorkItemSnapshot());
        await using var factory = EnabledFactory(store);

        using var response = await SendAsync(factory, "POST", WorkItems, """{"title":"Ship the thing","request":"Research and plan it."}""")
            .ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Created, response.StatusCode);
        AssertEx.NotNull(response.Headers.Location);
        await store.Received(1)
                   .CreateWorkItemAsync(Arg.Is<CreateDevWorkflowWorkItemCommand>(command => command.Title == "Ship the thing"
                                                                                            && command.Request == "Research and plan it."
                                                                                            && command.DevelopmentProjectId == null),
                       Arg.Any<CancellationToken>());
    }

    [Test]
    [Arguments("""{"title":"","request":"r"}""")]
    [Arguments("""{"title":"t","request":""}""")]
    public async Task CreateWorkItem_WhenTheShapeIsWrong_ReturnsBadRequestAndNeverReachesTheStore(string body)
    {
        var store = Store();
        await using var factory = EnabledFactory(store);

        using var response = await SendAsync(factory, "POST", WorkItems, body).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await store.DidNotReceive().CreateWorkItemAsync(Arg.Any<CreateDevWorkflowWorkItemCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetWorkItem_EmbedsItsRunSummaries()
    {
        var store = Store();
        store.GetWorkItemAsync(WorkItemId, Arg.Any<CancellationToken>()).Returns(WorkItemSnapshot());
        store.ListRunSummariesAsync(WorkItemId, Arg.Any<DevWorkflowRunStatus?>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([RunSummary()]);
        await using var factory = EnabledFactory(store);

        using var response = await SendAsync(factory, "GET", WorkItem).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        var run = document.RootElement.GetProperty("runs")[0];
        AssertEx.Equal(RunId, run.GetProperty("id").GetGuid());
        AssertEx.Equal("WaitingForApproval", run.GetProperty("status").GetString());
        AssertEx.Equal(1, run.GetProperty("pendingDecisionCount").GetInt32());
    }

    [Test]
    public async Task UpdateWorkItem_WithOnlyATitle_ForwardsTheOtherMemberAsUnchanged()
    {
        var store = Store();
        store.UpdateWorkItemAsync(Arg.Any<UpdateDevWorkflowWorkItemCommand>(), Arg.Any<CancellationToken>()).Returns(WorkItemSnapshot());
        store.ListRunSummariesAsync(WorkItemId, Arg.Any<DevWorkflowRunStatus?>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        await using var factory = EnabledFactory(store);

        using var response = await SendAsync(factory, "PATCH", WorkItem, """{"title":"renamed"}""").ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        await store.Received(1)
                   .UpdateWorkItemAsync(Arg.Is<UpdateDevWorkflowWorkItemCommand>(command => command.WorkItemId == WorkItemId
                                                                                            && command.Title == "renamed"
                                                                                            && command.Request == null),
                       Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeleteWorkItem_DelegatesToTheRuntimeSoTheOwnedSessionsAndBytesGoWithIt()
    {
        var runs = Substitute.For<IDevWorkflowRunService>();
        await using var factory = EnabledFactory(Store(), runs);

        using var response = await SendAsync(factory, "DELETE", WorkItem).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await runs.Received(1).DeleteWorkItemAsync(WorkItemId, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeleteWorkItem_WhileARunIsLive_ReturnsTheRunInFlightConflict()
    {
        var runs = Substitute.For<IDevWorkflowRunService>();
        runs.DeleteWorkItemAsync(WorkItemId, Arg.Any<CancellationToken>()).ThrowsAsyncForAnyArgs(new DevWorkflowRunInFlightException("still running"));
        await using var factory = EnabledFactory(Store(), runs);

        using var response = await SendAsync(factory, "DELETE", WorkItem).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        AssertEx.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(body);
        AssertEx.Equal("DevWorkflowRunInFlight", document.RootElement.GetProperty("conflictType").GetString());
    }

    [Test]
    [Arguments("GET", WorkItem, null)]
    [Arguments("PATCH", WorkItem, """{"title":"renamed"}""")]
    [Arguments("GET", Definition, null)]
    [Arguments("PUT", Definition, """{"version":1,"name":"renamed"}""")]
    [Arguments("DELETE", Definition, null)]
    [Arguments("GET", RuleSet, null)]
    [Arguments("PUT", RuleSet, """{"version":1,"name":"renamed","body":"Be careful."}""")]
    [Arguments("DELETE", RuleSet, null)]
    public async Task DevWorkflowRoute_WhenTheResourceIsUnknown_ReturnsBodylessNotFound(string method, string route, string? body)
    {
        var store = Store();
        var missing = new DevWorkflowNotFoundException("gone");
        store.GetWorkItemAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).ThrowsAsyncForAnyArgs(missing);
        store.UpdateWorkItemAsync(Arg.Any<UpdateDevWorkflowWorkItemCommand>(), Arg.Any<CancellationToken>()).ThrowsAsyncForAnyArgs(missing);
        store.GetDefinitionAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).ThrowsAsyncForAnyArgs(missing);
        store.UpdateDefinitionAsync(Arg.Any<UpdateDevWorkflowDefinitionCommand>(), Arg.Any<CancellationToken>()).ThrowsAsyncForAnyArgs(missing);
        store.ArchiveDefinitionAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).ThrowsAsyncForAnyArgs(missing);
        store.GetRuleSetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).ThrowsAsyncForAnyArgs(missing);
        store.UpdateRuleSetAsync(Arg.Any<UpdateDevWorkflowRuleSetCommand>(), Arg.Any<CancellationToken>()).ThrowsAsyncForAnyArgs(missing);
        store.DeleteRuleSetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).ThrowsAsyncForAnyArgs(missing);
        await using var factory = EnabledFactory(store);

        using var response = await SendAsync(factory, method, route, body).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode, $"{method} {route} must answer 404 for an unknown resource.");
        AssertEx.Null(response.Content.Headers.ContentType, $"{method} {route} must not attach a content type to a bodyless 404.");
        AssertEx.Equal(string.Empty, await response.Content.ReadAsStringAsync().ConfigureAwait(false), $"{method} {route} must answer a bodyless 404.");
    }

    [Test]
    public async Task ListDefinitions_ForwardsIncludeArchived()
    {
        var store = Store();
        store.ListDefinitionsAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns([DefinitionSummary()]);
        await using var factory = EnabledFactory(store);

        using var response = await SendAsync(factory, "GET", $"{Definitions}?includeArchived=true").ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        await store.Received(1).ListDefinitionsAsync(includeArchived: true, Arg.Any<CancellationToken>());
        using var document = JsonDocument.Parse(body);
        AssertEx.Equal("Seeded", document.RootElement.GetProperty("items")[0].GetProperty("source").GetString());
    }

    /// <summary>
    ///     The graph is a field-for-field mirror, so a definition read back carries every authored field — including
    ///     the ones only the editor uses.
    /// </summary>
    [Test]
    public async Task GetDefinition_RendersTheStoredGraphFieldForField()
    {
        var store = Store();
        store.GetDefinitionAsync(DefinitionId, Arg.Any<CancellationToken>()).Returns(DefinitionSnapshot());
        await using var factory = EnabledFactory(store);

        using var response = await SendAsync(factory, "GET", Definition).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        var graph = document.RootElement.GetProperty("graph");
        AssertEx.Equal(1, graph.GetProperty("schemaVersion").GetInt32());
        var research = graph.GetProperty("nodes")[0];
        AssertEx.Equal("research", research.GetProperty("nodeKey").GetString());
        AssertEx.Equal("Agent", research.GetProperty("nodeType").GetString());
        AssertEx.Equal("researcher", research.GetProperty("agentSeedSlug").GetString());
        AssertEx.Equal(3, research.GetProperty("maxAttempts").GetInt32());
        AssertEx.Equal("approval", graph.GetProperty("edges")[0].GetProperty("to").GetString());
    }

    [Test]
    public async Task CreateDefinition_ValidatesTheGraphWithTheRuntimesOwnParserAndStoresItsNodeCount()
    {
        var store = Store();
        store.CreateDefinitionAsync(Arg.Any<CreateDevWorkflowDefinitionCommand>(), Arg.Any<CancellationToken>()).Returns(DefinitionSnapshot());
        await using var factory = EnabledFactory(store);

        using var response = await SendAsync(factory, "POST", Definitions, CreateDefinitionBody(SampleGraph)).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Created, response.StatusCode);
        await store.Received(1)
                   .CreateDefinitionAsync(Arg.Is<CreateDevWorkflowDefinitionCommand>(command => command.Name == "Research → Plan → Approval"
                                                                                                && command.NodeCount == 2),
                       Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     Save-time validation is the run-start parser, so the rules cannot diverge: a graph the dispatcher could not
    ///     route is refused here, with the parser's own message.
    /// </summary>
    [Test]
    [Arguments(
        """{"schemaVersion":1,"nodes":[{"nodeKey":"a","nodeType":"Agent"},{"nodeKey":"b","nodeType":"Agent"},{"nodeKey":"c","nodeType":"Agent"}],"edges":[{"from":"a","to":"b"},{"from":"b","to":"c"},{"from":"c","to":"b"}]}""",
        "cycle")]
    [Arguments("""{"schemaVersion":1,"nodes":[{"nodeKey":"a","nodeType":"Agent"},{"nodeKey":"b","nodeType":"Agent"}],"edges":[]}""", "entry node")]
    [Arguments("""{"schemaVersion":1,"nodes":[{"nodeKey":"a","nodeType":"Nonsense"}],"edges":[]}""", "'nodeType'")]
    [Arguments("""{"schemaVersion":1,"nodes":[],"edges":[]}""", "at least one node")]
    public async Task CreateDefinition_WithAGraphNothingCouldRoute_ReturnsBadRequestAndNeverReachesTheStore(string graph, string expectedMessage)
    {
        var store = Store();
        await using var factory = EnabledFactory(store);

        using var response = await SendAsync(factory, "POST", Definitions, CreateDefinitionBody(graph)).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertEx.Contains(body, "generalErrors", StringComparison.Ordinal);
        AssertEx.Contains(body, expectedMessage, StringComparison.Ordinal);
        await store.DidNotReceive().CreateDefinitionAsync(Arg.Any<CreateDevWorkflowDefinitionCommand>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     H2: a boolean condition value must survive the round trip as a boolean. Stringified, it would compare
    ///     against a real boolean as a type mismatch, the evaluator would fail closed, and the edge would silently
    ///     never fire — with nothing in the log to say why.
    /// </summary>
    [Test]
    public async Task Definition_KeepsAConditionValuesJsonTypeThroughTheRoundTrip()
    {
        const string ConditionGraph = """
                                      {"schemaVersion":1,
                                       "nodes":[{"nodeKey":"a","nodeType":"Agent"},{"nodeKey":"b","nodeType":"Agent"}],
                                       "edges":[{"from":"a","to":"b","condition":{"path":"passed","op":"eq","value":true}}]}
                                      """;
        var store = Store();
        string? stored = null;
        store.CreateDefinitionAsync(Arg.Any<CreateDevWorkflowDefinitionCommand>(), Arg.Any<CancellationToken>())
             .Returns(call =>
             {
                 stored = call.Arg<CreateDevWorkflowDefinitionCommand>().GraphJson;
                 return DefinitionSnapshot(stored);
             });
        await using var factory = EnabledFactory(store);

        using var response = await SendAsync(factory, "POST", Definitions, CreateDefinitionBody(ConditionGraph)).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Created, response.StatusCode);
        AssertEx.NotNull(stored);
        using var storedDocument = JsonDocument.Parse(stored!);
        AssertEx.Equal(JsonValueKind.True,
            storedDocument.RootElement.GetProperty("edges")[0].GetProperty("condition").GetProperty("value").ValueKind,
            "the stored graph must keep the boolean, not a string spelling of one.");
        using var document = JsonDocument.Parse(body);
        AssertEx.Equal(JsonValueKind.True, document.RootElement.GetProperty("graph").GetProperty("edges")[0].GetProperty("condition").GetProperty("value").ValueKind);
    }

    /// <summary>
    ///     L2: <c>toolMode</c> is what makes a Tool node the one that APPLIES approved patches, and it was not on the
    ///     wire at all — so a copy of the seeded template answered 201 and silently came back with an ordinary
    ///     validation node where the integration step had been. Both directions are asserted on one round trip: what
    ///     the store was handed, and what the caller reads back. Sent in lower case on purpose: the stored value is
    ///     canonical whatever an author writes, so nothing reading the blob later has to parse case-insensitively.
    ///     <para>
    ///         The <c>validate</c> node is not decoration: <c>GRAPH-C4-3</c> refuses an apply a run can reach without a
    ///         validation, and the seeded template this graph models has always had one between the work and the gate.
    ///     </para>
    /// </summary>
    [Test]
    public async Task Definition_KeepsAnApplyNodesToolModeThroughTheRoundTrip()
    {
        const string ApplyGraph = """
                                  {"schemaVersion":1,
                                   "nodes":[{"nodeKey":"implement","nodeType":"DevTask","nodeTimeoutSeconds":900},
                                            {"nodeKey":"validate","nodeType":"Tool"},
                                            {"nodeKey":"approval","nodeType":"HumanGate"},
                                            {"nodeKey":"integrate","nodeType":"Tool","toolMode":"apply","label":"Apply the approved patches"}],
                                   "edges":[{"from":"implement","to":"validate"},
                                            {"from":"validate","to":"approval"},
                                            {"from":"approval","to":"integrate","condition":{"path":"decision","op":"eq","value":"Approve"}}]}
                                  """;
        var store = Store();
        string? stored = null;
        store.CreateDefinitionAsync(Arg.Any<CreateDevWorkflowDefinitionCommand>(), Arg.Any<CancellationToken>())
             .Returns(call =>
             {
                 stored = call.Arg<CreateDevWorkflowDefinitionCommand>().GraphJson;
                 return DefinitionSnapshot(stored);
             });
        await using var factory = EnabledFactory(store);

        using var response = await SendAsync(factory, "POST", Definitions, CreateDefinitionBody(ApplyGraph)).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Created, response.StatusCode);
        AssertEx.NotNull(stored);
        AssertEx.Contains(stored!,
            "\"toolMode\":\"Apply\"",
            StringComparison.Ordinal,
            "the stored graph is what a run pins, so the apply node survives into it — in the parser's own spelling, whatever casing was sent.");
        using var document = JsonDocument.Parse(body);
        var nodes = document.RootElement.GetProperty("graph").GetProperty("nodes");
        AssertEx.Equal("Apply", nodes[3].GetProperty("toolMode").GetString());
        AssertEx.Equal(JsonValueKind.Null,
            nodes[1].GetProperty("toolMode").ValueKind,
            "a node that declares none reads back as null — absent is Validate, exactly as the runtime's parser reads it.");
        using var storedDocument = JsonDocument.Parse(stored!);
        AssertEx.False(storedDocument.RootElement.GetProperty("nodes")[1].TryGetProperty("toolMode", out _),
            "and nothing is written into the stored graph for it, so a definition authored before this field keeps its bytes.");
    }

    /// <summary>
    ///     The capability fields cross the wire in BOTH directions, or they are not authorable at all: the mapper
    ///     serializes the wire DTO, so a graph field absent from it is DROPPED on any save round-trip — which is how
    ///     <c>toolMode</c> was lost once already. All three are asserted on one trip: the node's declared effects and
    ///     its loop cap, and the graph-level waiver, in what the store was handed and in what the caller reads back.
    /// </summary>
    [Test]
    public async Task Definition_KeepsTheCapabilityFieldsThroughTheRoundTrip()
    {
        const string CapabilityGraph = """
                                       {"schemaVersion":1,"allowUngatedWrites":true,
                                        "nodes":[{"nodeKey":"implement","nodeType":"Agent",
                                                  "requiredCapabilities":{"WriteExecute":"runs the release script"}},
                                                 {"nodeKey":"check","nodeType":"Tool","retryTarget":"implement","maxLoopIterations":2}],
                                        "edges":[{"from":"implement","to":"check"}]}
                                       """;
        var store = Store();
        string? stored = null;
        store.CreateDefinitionAsync(Arg.Any<CreateDevWorkflowDefinitionCommand>(), Arg.Any<CancellationToken>())
             .Returns(call =>
             {
                 stored = call.Arg<CreateDevWorkflowDefinitionCommand>().GraphJson;
                 return DefinitionSnapshot(stored);
             });
        await using var factory = EnabledFactory(store);

        using var response = await SendAsync(factory, "POST", Definitions, CreateDefinitionBody(CapabilityGraph)).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Created, response.StatusCode);
        AssertEx.NotNull(stored);
        using var storedDocument = JsonDocument.Parse(stored!);
        AssertEx.True(storedDocument.RootElement.GetProperty("allowUngatedWrites").GetBoolean(), "the waiver is what a run pins, so it has to survive into the blob.");
        var storedNodes = storedDocument.RootElement.GetProperty("nodes");
        AssertEx.Equal("runs the release script",
            storedNodes[0].GetProperty("requiredCapabilities").GetProperty("WriteExecute").GetString(),
            "the author's reason rides the blob untouched — nothing in the runtime reads it, and the editor renders it.");
        AssertEx.Equal(expected: 2, storedNodes[1].GetProperty("maxLoopIterations").GetInt32());
        AssertEx.False(storedNodes[0].TryGetProperty("maxLoopIterations", out _),
            "and a node that names no cap writes none, so a definition authored before this field keeps its bytes.");

        using var document = JsonDocument.Parse(body);
        var graph = document.RootElement.GetProperty("graph");
        AssertEx.True(graph.GetProperty("allowUngatedWrites").GetBoolean());
        var nodes = graph.GetProperty("nodes");
        AssertEx.Equal("runs the release script", nodes[0].GetProperty("requiredCapabilities").GetProperty("WriteExecute").GetString());
        AssertEx.Equal(expected: 2, nodes[1].GetProperty("maxLoopIterations").GetInt32());
    }

    /// <summary>
    ///     And the parse rules apply to an API-authored apply node exactly as they do to the seeded one: an apply the
    ///     graph does not put a human gate in front of is refused at save time, with the parser's own sentence.
    /// </summary>
    /// <summary>
    ///     <c>isTemplate</c> is DERIVED, so it rides the response and never the blob: a definition read back, edited and
    ///     PUT must store exactly the bytes it arrived with, and a persisted copy would be a second answer able to
    ///     disagree with the parser the dispatcher admits by. Both halves of the round trip are asserted.
    /// </summary>
    [Test]
    public async Task Definition_ReportsIsTemplateOnTheWireAndNeverWritesItIntoTheStoredGraph()
    {
        const string DecompositionGraph = """
                                          {"schemaVersion":1,
                                           "nodes":[{"nodeKey":"decompose","nodeType":"Agent","label":"Decompose","isTemplate":true,
                                                     "materialization":{"templateNodeKey":"implement","artifactKind":"TaskPackage","joinNodeKey":"join","maxChildren":4}},
                                                    {"nodeKey":"implement","nodeType":"DevTask"},
                                                    {"nodeKey":"join","nodeType":"Join"}],
                                           "edges":[{"from":"decompose","to":"join"},{"from":"implement","to":"join"}]}
                                          """;
        var store = Store();
        string? stored = null;
        store.UpdateDefinitionAsync(Arg.Any<UpdateDevWorkflowDefinitionCommand>(), Arg.Any<CancellationToken>())
             .Returns(call =>
             {
                 stored = call.Arg<UpdateDevWorkflowDefinitionCommand>().GraphJson;
                 return DefinitionSnapshot(stored);
             });
        await using var factory = EnabledFactory(store);

        using var response = await SendAsync(factory,
                "PUT",
                Definition,
                $$"""{"version":4,"name":"renamed","graph":{{DecompositionGraph}}}""")
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.NotNull(stored);
        AssertEx.False(stored!.Contains("isTemplate", StringComparison.OrdinalIgnoreCase),
            $"the stored graph is what a run pins, and it must carry no derived field — not even the one an author SENT: {stored}");

        using var document = JsonDocument.Parse(body);
        var nodes = document.RootElement.GetProperty("graph").GetProperty("nodes").EnumerateArray().ToArray();
        AssertEx.False(nodes.Single(static node => node.GetProperty("nodeKey").GetString() == "decompose").GetProperty("isTemplate").GetBoolean(),
            "and the response answers from the parser, not from what the author claimed on the way in.");
        AssertEx.True(nodes.Single(static node => node.GetProperty("nodeKey").GetString() == "implement").GetProperty("isTemplate").GetBoolean(),
            "the template subtree is what the runtime gives no node run to, and the client must not walk the graph again to find it.");
        AssertEx.False(nodes.Single(static node => node.GetProperty("nodeKey").GetString() == "join").GetProperty("isTemplate").GetBoolean());
    }

    [Test]
    public async Task CreateDefinition_WithAnUngatedApplyNode_ReturnsBadRequestAndNeverReachesTheStore()
    {
        const string UngatedApply = """
                                    {"schemaVersion":1,
                                     "nodes":[{"nodeKey":"implement","nodeType":"DevTask","nodeTimeoutSeconds":900},
                                              {"nodeKey":"integrate","nodeType":"Tool","toolMode":"Apply"}],
                                     "edges":[{"from":"implement","to":"integrate"}]}
                                    """;
        var store = Store();
        await using var factory = EnabledFactory(store);

        using var response = await SendAsync(factory, "POST", Definitions, CreateDefinitionBody(UngatedApply)).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertEx.Contains(body, "reached from something other than a human gate", StringComparison.Ordinal);
        await store.DidNotReceive().CreateDefinitionAsync(Arg.Any<CreateDevWorkflowDefinitionCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateDefinition_WithoutAGraph_LeavesTheStoredOneAlone()
    {
        var store = Store();
        store.UpdateDefinitionAsync(Arg.Any<UpdateDevWorkflowDefinitionCommand>(), Arg.Any<CancellationToken>()).Returns(DefinitionSnapshot());
        await using var factory = EnabledFactory(store);

        using var response = await SendAsync(factory, "PUT", Definition, """{"version":4,"name":"renamed"}""").ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        await store.Received(1)
                   .UpdateDefinitionAsync(Arg.Is<UpdateDevWorkflowDefinitionCommand>(command => command.ExpectedVersion == 4
                                                                                                && command.Name == "renamed"
                                                                                                && command.GraphJson == null
                                                                                                && command.NodeCount == null),
                       Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     <c>Version</c> is a <c>required</c> member, so an omitted one is refused by the JSON binder rather than by a
    ///     validator. That is the arm worth pinning: a required-member miss that escapes the binder is a 500, and this
    ///     endpoint answers the same 400 a bad value gets.
    /// </summary>
    [Test]
    [Arguments("""{"name":"renamed"}""")]
    [Arguments("""{"version":0,"name":"renamed"}""")]
    public async Task UpdateDefinition_WithoutAUsableVersion_ReturnsBadRequestAndNeverReachesTheStore(string body)
    {
        var store = Store();
        await using var factory = EnabledFactory(store);

        using var response = await SendAsync(factory, "PUT", Definition, body).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode, responseBody);
        AssertEx.Contains(responseBody, "version", StringComparison.OrdinalIgnoreCase, responseBody);
        await store.DidNotReceive().UpdateDefinitionAsync(Arg.Any<UpdateDevWorkflowDefinitionCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateDefinition_WhenTheVersionIsStale_ReturnsTheVersionConflict()
    {
        var store = Store();
        store.UpdateDefinitionAsync(Arg.Any<UpdateDevWorkflowDefinitionCommand>(), Arg.Any<CancellationToken>())
             .ThrowsAsyncForAnyArgs(new DevWorkflowConcurrencyException("The definition moved on."));
        await using var factory = EnabledFactory(store);

        using var response = await SendAsync(factory, "PUT", Definition, """{"version":1,"name":"renamed"}""").ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        AssertEx.Equal("DevWorkflowVersionConflict", document.RootElement.GetProperty("conflictType").GetString());
    }

    /// <summary>Delete archives, so a run that pinned the definition keeps rendering and the id never becomes undeletable.</summary>
    [Test]
    public async Task DeleteDefinition_ArchivesRatherThanRemoving()
    {
        var store = Store();
        store.ArchiveDefinitionAsync(DefinitionId, Arg.Any<CancellationToken>()).Returns(DefinitionSnapshot());
        await using var factory = EnabledFactory(store);

        using var response = await SendAsync(factory, "DELETE", Definition).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await store.Received(1).ArchiveDefinitionAsync(DefinitionId, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ListRuleSets_RendersTheScopeAndNeverTheBody()
    {
        var store = Store();
        store.ListRuleSetsAsync(Arg.Any<CancellationToken>()).Returns([RuleSetSummary()]);
        await using var factory = EnabledFactory(store);

        using var response = await SendAsync(factory, "GET", RuleSets).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        var item = document.RootElement.GetProperty("items")[0];
        AssertEx.Equal("House rules", item.GetProperty("name").GetString());
        AssertEx.Equal("Agent", item.GetProperty("scope").GetProperty("nodeTypes")[0].GetString());
        AssertEx.Equal(WorkItemId, item.GetProperty("scope").GetProperty("projectIds")[0].GetGuid());
        AssertEx.False(item.TryGetProperty("body", out _), "the list is drawn without decrypting a single body.");
    }

    [Test]
    public async Task CreateRuleSet_StoresTheScopeAsTheResolverReadsItAndAnswersCreated()
    {
        var store = Store();
        store.CreateRuleSetAsync(Arg.Any<CreateDevWorkflowRuleSetCommand>(), Arg.Any<CancellationToken>()).Returns(RuleSetSnapshot());
        await using var factory = EnabledFactory(store);

        var request = $$$"""{"name":"House rules","description":"What every agent follows.","body":"Never touch production.","scope":{"projectIds":["{{{WorkItemId}}}"],"nodeTypes":["agent"]}}""";

        using var response = await SendAsync(factory, "POST", RuleSets, request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Created, response.StatusCode);
        using (var created = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false)))
        {
            AssertEx.Equal("Never touch production.",
                created.RootElement.GetProperty("body").GetString(),
                "the 201 carries the store SNAPSHOT's plaintext body — the store decrypts on read, so a response echoing ciphertext would mean the mapper read an entity.");
        }

        AssertEx.Equal($"/api/local/v1/development-workflows/rule-sets/{RuleSetId}",
            response.Headers.Location?.ToString(),
            "a 201 names where the created rule set can be read.");
        await store.Received(1)
                   .CreateRuleSetAsync(Arg.Is<CreateDevWorkflowRuleSetCommand>(command => command.Name == "House rules"
                                                                                          && command.Body == "Never touch production."
                                                                                          && command.Enabled
                                                                                          && command.ScopeJson.Contains(WorkItemId.ToString(), StringComparison.Ordinal)
                                                                                          && command.ScopeJson.Contains("\"agent\"", StringComparison.Ordinal)),
                       Arg.Any<CancellationToken>());
    }

    /// <summary>An omitted scope is BOTH axes empty, which is the document's own spelling of "applies everywhere".</summary>
    [Test]
    public async Task CreateRuleSet_WithNoScope_StoresTheEmptyScopeRatherThanNothing()
    {
        var store = Store();
        store.CreateRuleSetAsync(Arg.Any<CreateDevWorkflowRuleSetCommand>(), Arg.Any<CancellationToken>()).Returns(RuleSetSnapshot());
        await using var factory = EnabledFactory(store);

        using var response = await SendAsync(factory, "POST", RuleSets, """{"name":"House rules","body":"Never touch production."}""").ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Created, response.StatusCode);
        await store.Received(1)
                   .CreateRuleSetAsync(Arg.Is<CreateDevWorkflowRuleSetCommand>(command => command.ScopeJson == """{"projectIds":[],"nodeTypes":[]}"""),
                       Arg.Any<CancellationToken>());
    }

    [Test]
    [Arguments("""{"body":"Never touch production."}""", "needs a name")]
    [Arguments("""{"name":"House rules"}""", "needs a body")]
    [Arguments("""{"name":"House rules","body":"x","scope":{"projectIds":[],"nodeTypes":["Nonsense"]}}""", "scope.nodeTypes")]
    [Arguments("""{"name":"House rules","body":"x","scope":{"projectIds":[],"nodeTypes":["3"]}}""", "scope.nodeTypes")]
    [Arguments("""{"name":"House rules","body":"x","scope":{"projectIds":[],"nodeTypes":["-1"]}}""", "scope.nodeTypes")]
    public async Task CreateRuleSet_WithAMalformedBody_ReturnsBadRequestAndNeverReachesTheStore(string body, string expectedMessage)
    {
        var store = Store();
        await using var factory = EnabledFactory(store);

        using var response = await SendAsync(factory, "POST", RuleSets, body).ConfigureAwait(false);
        var problem = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertEx.Contains(problem, expectedMessage, StringComparison.Ordinal);
        await store.DidNotReceive().CreateRuleSetAsync(Arg.Any<CreateDevWorkflowRuleSetCommand>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     The body is bounded well below what an objective can carry, so a rule set the operator believes is in force
    ///     cannot be one the agent only ever reads half of. 4096 is accepted; one character more is refused by name.
    /// </summary>
    [Test]
    public async Task CreateRuleSet_WithABodyPastTheLimit_IsRefusedAndTheLimitItselfIsAccepted()
    {
        var store = Store();
        store.CreateRuleSetAsync(Arg.Any<CreateDevWorkflowRuleSetCommand>(), Arg.Any<CancellationToken>()).Returns(RuleSetSnapshot());
        await using var factory = EnabledFactory(store);

        using var refused = await SendAsync(factory, "POST", RuleSets, $$"""{"name":"House rules","body":"{{new string('a', 4097)}}"}""").ConfigureAwait(false);
        var problem = await refused.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        AssertEx.Contains(problem, "4096-character limit", StringComparison.Ordinal);
        await store.DidNotReceive().CreateRuleSetAsync(Arg.Any<CreateDevWorkflowRuleSetCommand>(), Arg.Any<CancellationToken>());

        using var accepted = await SendAsync(factory, "POST", RuleSets, $$"""{"name":"House rules","body":"{{new string('a', 4096)}}"}""").ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Created, accepted.StatusCode, "the bound itself is inclusive.");
    }

    /// <summary>
    ///     A scope column nothing can parse renders as empty axes rather than failing the read. The resolver already
    ///     treats such a row as applying to NOTHING, and a page that cannot load it is a page nobody can use to fix it.
    /// </summary>
    [Test]
    public async Task GetRuleSet_WithAnUnreadableStoredScope_RendersEmptyAxesRatherThanFailing()
    {
        var store = Store();
        store.GetRuleSetAsync(RuleSetId, Arg.Any<CancellationToken>()).Returns(RuleSetSnapshot() with
        {
            ScopeJson = "not json at all"
        });
        await using var factory = EnabledFactory(store);

        using var response = await SendAsync(factory, "GET", RuleSet).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        var scope = document.RootElement.GetProperty("scope");
        AssertEx.Equal(expected: 0, scope.GetProperty("projectIds").GetArrayLength());
        AssertEx.Equal(expected: 0, scope.GetProperty("nodeTypes").GetArrayLength());
    }

    [Test]
    public async Task GetRuleSet_CarriesTheBodyAndTheHashThatNamesIt()
    {
        var store = Store();
        store.GetRuleSetAsync(RuleSetId, Arg.Any<CancellationToken>()).Returns(RuleSetSnapshot());
        await using var factory = EnabledFactory(store);

        using var response = await SendAsync(factory, "GET", RuleSet).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        AssertEx.Equal("Never touch production.", document.RootElement.GetProperty("body").GetString());
        AssertEx.Equal("content-hash", document.RootElement.GetProperty("contentSha256").GetString());
        AssertEx.True(document.RootElement.GetProperty("enabled").GetBoolean());
    }

    [Test]
    public async Task UpdateRuleSet_ReplacesTheWholeDocumentAtTheVersionItWasEditedFrom()
    {
        var store = Store();
        store.UpdateRuleSetAsync(Arg.Any<UpdateDevWorkflowRuleSetCommand>(), Arg.Any<CancellationToken>()).Returns(RuleSetSnapshot());
        await using var factory = EnabledFactory(store);

        using var response = await SendAsync(factory, "PUT", RuleSet, """{"version":4,"name":"renamed","body":"Read the plan first.","enabled":false}""")
            .ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        using (var updated = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false)))
        {
            AssertEx.Equal("Never touch production.", updated.RootElement.GetProperty("body").GetString(), "and so does the 200 a PUT answers with.");
        }

        await store.Received(1)
                   .UpdateRuleSetAsync(Arg.Is<UpdateDevWorkflowRuleSetCommand>(command => command.ExpectedVersion == 4
                                                                                          && command.Name == "renamed"
                                                                                          && command.Body == "Read the plan first."
                                                                                          && command.Description == null
                                                                                          && !command.Enabled),
                       Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateRuleSet_WhenTheVersionIsStale_ReturnsTheVersionConflict()
    {
        var store = Store();
        store.UpdateRuleSetAsync(Arg.Any<UpdateDevWorkflowRuleSetCommand>(), Arg.Any<CancellationToken>())
             .ThrowsAsyncForAnyArgs(new DevWorkflowConcurrencyException("The rule set moved on."));
        await using var factory = EnabledFactory(store);

        using var response = await SendAsync(factory, "PUT", RuleSet, """{"version":1,"name":"renamed","body":"Read the plan first."}""").ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        AssertEx.Equal("DevWorkflowVersionConflict", document.RootElement.GetProperty("conflictType").GetString());
    }

    /// <summary>
    ///     A hard delete, and it does not refuse while a run is in flight: every node run that applied this rule set
    ///     copied its {id, name, contentSha256} onto its own row at materialization, so the audit outlives the document.
    /// </summary>
    [Test]
    public async Task DeleteRuleSet_RemovesItOutrightAndAnswersNoContent()
    {
        var store = Store();
        await using var factory = EnabledFactory(store);

        using var response = await SendAsync(factory, "DELETE", RuleSet).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await store.Received(1).DeleteRuleSetAsync(RuleSetId, Arg.Any<CancellationToken>());
    }

    private static DevWorkflowRuleSetSnapshot RuleSetSnapshot() =>
        new(RuleSetId,
            "House rules",
            "What every agent follows.",
            $$"""{"projectIds":["{{WorkItemId}}"],"nodeTypes":["Agent"]}""",
            Enabled: true,
            "Never touch production.",
            "content-hash",
            Version: 4,
            CreatedAtUtc: 1,
            UpdatedAtUtc: 2);

    private static DevWorkflowRuleSetSummary RuleSetSummary() =>
        new(RuleSetId,
            "House rules",
            "What every agent follows.",
            $$"""{"projectIds":["{{WorkItemId}}"],"nodeTypes":["Agent"]}""",
            Enabled: true,
            "content-hash",
            Version: 4,
            CreatedAtUtc: 1,
            UpdatedAtUtc: 2);

    private static string CreateDefinitionBody(string graph) =>
        $$"""{"name":"Research → Plan → Approval","graph":{{graph}}}""";

    private static IDevWorkflowStore Store()
    {
        var store = Substitute.For<IDevWorkflowStore>();
        store.ListWorkItemsAsync(Arg.Any<DevWorkflowWorkItemStatus?>(), Arg.Any<CancellationToken>()).Returns([]);
        store.ListDefinitionsAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns([]);
        store.ListRuleSetsAsync(Arg.Any<CancellationToken>()).Returns([]);
        store.ListRunSummariesAsync(Arg.Any<Guid?>(), Arg.Any<DevWorkflowRunStatus?>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        return store;
    }

    private static DevWorkflowWorkItemSnapshot WorkItemSnapshot() =>
        new(WorkItemId,
            "Ship the thing",
            "Research and plan it.",
            DevWorkflowWorkItemStatus.Active,
            DevelopmentProjectId: null,
            RunId,
            DevWorkflowRunStatus.WaitingForApproval,
            "Research → Plan → Approval",
            new DevWorkflowNodeCounters(Queued: 0, Running: 1, Completed: 1, Total: 2, PendingDecisionCount: 1, RunId),
            CreatedAtUtc: 10,
            UpdatedAtUtc: 20,
            Version: 3);

    private static DevWorkflowRunSummary RunSummary() =>
        new(RunId,
            WorkItemId,
            DefinitionId,
            "Research → Plan → Approval",
            DevWorkflowRunStatus.WaitingForApproval,
            new DevWorkflowNodeCounters(Queued: 0, Running: 1, Completed: 1, Total: 2, PendingDecisionCount: 1, RunId),
            FailureClass: null,
            StartedAtUtc: 11,
            EndedAtUtc: null,
            CreatedAtUtc: 10,
            UpdatedAtUtc: 20);

    private static DevWorkflowDefinitionSnapshot DefinitionSnapshot(string? graphJson = null) =>
        new(DefinitionId,
            "Research → Plan → Approval",
            graphJson ?? SampleGraph,
            "graph-hash",
            NodeCount: 2,
            DevWorkflowDefinitionSource.Seeded,
            "research-plan-approval",
            Archived: false,
            Version: 4,
            CreatedAtUtc: 1,
            UpdatedAtUtc: 2);

    private static DevWorkflowDefinitionSummary DefinitionSummary() =>
        new(DefinitionId,
            "Research → Plan → Approval",
            "graph-hash",
            NodeCount: 2,
            DevWorkflowDefinitionSource.Seeded,
            "research-plan-approval",
            Archived: false,
            Version: 4,
            CreatedAtUtc: 1,
            UpdatedAtUtc: 2);

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

    private static TestServerWebAppFactory EnabledFactory(IDevWorkflowStore store, IDevWorkflowRunService? runs = null) =>
        new()
        {
            AdditionalConfiguration = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["DevWorkflows:Enabled"] = "true"
            },
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<IDevWorkflowStore>();
                services.AddSingleton(store);
                services.RemoveAll<IDevWorkflowRunService>();
                services.AddSingleton(runs ?? Substitute.For<IDevWorkflowRunService>());
            }
        };
}

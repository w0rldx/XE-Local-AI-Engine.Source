namespace XE_Local_AI_Engine.Tests.Endpoints.GraphWorkflows.V1;

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Tests.GraphWorkflows;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The run half of the graph-workflow surface: the six routes a run is started, watched and cancelled through.
///     <para>
///         Driven over a REAL store and a real database rather than a substituted one, because what these routes are
///         worth proving is the round trip — a start that answers 202 and a second start with the same request id that
///         answers the same run id are one story about the unique index, not about a mapper.
///     </para>
/// </summary>
[Category(TestCategories.Integration)]
public sealed class GraphWorkflowRunEndpointTests
{
    private const string Root = "/api/local/v1/graph-workflows";

    private const string Runs = $"{Root}/runs";

    /// <summary>A route shape for the auth and feature-gate sweeps; the ids need not exist, because none of those reach a store.</summary>
    private const string Run = $"{Runs}/33333333-3333-3333-3333-333333333333";

    private const string DefinitionRuns = $"{Root}/definitions/22222222-2222-2222-2222-222222222222/runs";

    [ClassDataSource<GraphWorkflowHostFixture>(Shared = SharedType.PerClass)]
    public required GraphWorkflowHostFixture Host { get; init; }

    [Test]
    [Arguments("POST", DefinitionRuns)]
    [Arguments("GET", Runs)]
    [Arguments("GET", Run)]
    [Arguments("POST", $"{Run}/cancel")]
    [Arguments("GET", $"{Run}/nodes/analyze")]
    [Arguments("GET", $"{Run}/events")]
    public async Task Route_WhenTheOperatorTokenIsMissing_ReturnsUnauthorized(string method, string route)
    {
        using var client = Host.Factory.CreateClient();
        using var request = Request(method, route);

        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode, $"{method} {route} must require the operator token.");
    }

    [Test]
    [Arguments("POST", DefinitionRuns)]
    [Arguments("GET", Runs)]
    [Arguments("GET", Run)]
    [Arguments("POST", $"{Run}/cancel")]
    [Arguments("GET", $"{Run}/nodes/analyze")]
    [Arguments("GET", $"{Run}/events")]
    public async Task Route_WithANonOperatorToken_ReturnsForbidden(string method, string route)
    {
        using var client = Host.Factory.CreateClient();
        using var request = Request(method, route);
        Host.Factory.AddNonOperatorBearerToken(request);

        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.Forbidden, response.StatusCode, $"{method} {route} is operator-only, so an authenticated non-operator is refused.");
    }

    /// <summary>
    ///     The whole family disappears on a disabled node — 404 ahead of auth, indistinguishable from a wrong route, so
    ///     the switch cannot be probed by status code.
    /// </summary>
    [Test]
    [Arguments("POST", DefinitionRuns)]
    [Arguments("GET", Runs)]
    [Arguments("GET", Run)]
    [Arguments("POST", $"{Run}/cancel")]
    [Arguments("GET", $"{Run}/nodes/analyze")]
    [Arguments("GET", $"{Run}/events")]
    public async Task Route_WhenTheFeatureIsDisabled_ReturnsNotFound(string method, string route)
    {
        await using var factory = new TestServerWebAppFactory
        {
            AdditionalConfiguration = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["GraphWorkflows:Enabled"] = "false"
            }
        };

        using var client = factory.CreateClient();
        using var request = Request(method, route);
        factory.AddNodeBearerToken(request);

        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode, $"{method} {route} must answer 404 on a disabled node, never 500.");
    }

    /// <summary>
    ///     202, not 200: the endpoint commits a durable intent and the dispatcher advances it out of band. The same
    ///     request id answers the same run id, which is the whole point of a caller-minted key.
    /// </summary>
    [Test]
    public async Task StartRun_Answers202WithTheRunId_AndARetryAnswersTheSameOne()
    {
        var definitionId = await SeedDefinitionAsync(GraphWorkflowGraphs.StartAgentEnd);
        var requestId = Guid.NewGuid();
        var body = JsonSerializer.Serialize(new
        {
            requestId,
            input = new
            {
                topic = "latency"
            }
        });

        using var first = await SendAsync("POST", $"{Root}/definitions/{definitionId}/runs", body);
        using var second = await SendAsync("POST", $"{Root}/definitions/{definitionId}/runs", body);

        AssertEx.Equal(HttpStatusCode.Accepted, first.StatusCode);
        AssertEx.Equal(HttpStatusCode.Accepted, second.StatusCode);
        var firstRunId = await RunIdAsync(first);
        AssertEx.Equal(firstRunId, await RunIdAsync(second), "the same request id resolves to the run it already started.");
        AssertEx.NotEqual(Guid.Empty, firstRunId);
    }

    [Test]
    public async Task StartRun_WithNoRequestId_Answers400()
    {
        var definitionId = await SeedDefinitionAsync(GraphWorkflowGraphs.StartAgentEnd);

        using var response = await SendAsync("POST", $"{Root}/definitions/{definitionId}/runs", "{}");

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode, "an unminted idempotency key is a caller mistake, not a conflict.");
    }

    /// <summary>
    ///     A stale <c>definitionVersion</c> is one of the two ways a run command loses, and it reaches the client as the
    ///     409 discriminator the SPA branches on.
    /// </summary>
    [Test]
    public async Task StartRun_WithAStaleDefinitionVersion_Answers409WithTheRunConflictDiscriminator()
    {
        var definitionId = await SeedDefinitionAsync(GraphWorkflowGraphs.StartAgentEnd);
        var body = JsonSerializer.Serialize(new
        {
            requestId = Guid.NewGuid(),
            definitionVersion = 99
        });

        using var response = await SendAsync("POST", $"{Root}/definitions/{definitionId}/runs", body);

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        AssertEx.Equal("GraphWorkflowRunConflict", await ConflictTypeAsync(response));
    }

    /// <summary>The other way: a cancel of a run that has already finished. Same story, same discriminator.</summary>
    [Test]
    public async Task CancelRun_OnATerminalRun_Answers409WithTheRunConflictDiscriminator()
    {
        var definitionId = await SeedDefinitionAsync(GraphWorkflowGraphs.StartAgentEnd);
        var runId = await StartRunAsync(definitionId);

        // Driven terminal through the store: there is no dispatcher in this slice to finish a run on its own.
        await using (var scope = Host.Factory.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IGraphWorkflowStore>();
            var run = await store.GetRunAsync(runId);
            _ = await store.TransitionRunAsync(new TransitionGraphWorkflowRunCommand(runId,
                               run.Version,
                               GraphWorkflowRunStatus.Failed,
                               GraphWorkflowFailureClass.NodeFailed));
        }

        using var response = await SendAsync("POST", $"{Runs}/{runId}/cancel", "{}");

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        AssertEx.Equal("GraphWorkflowRunConflict", await ConflictTypeAsync(response));
    }

    [Test]
    public async Task CancelRun_OnALiveRun_Answers202AndTheBodyReadsCancelling()
    {
        var definitionId = await SeedDefinitionAsync(GraphWorkflowGraphs.StartAgentEnd);
        var runId = await StartRunAsync(definitionId);

        using var response = await SendAsync("POST", $"{Runs}/{runId}/cancel", "{}");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        AssertEx.Equal(HttpStatusCode.Accepted, response.StatusCode, "cancel is fire-and-forget, so it is accepted rather than done.");
        AssertEx.Equal("Cancelling", document.RootElement.GetProperty("run").GetProperty("status").GetString());
    }

    /// <summary>
    ///     A repeat cancel is idempotent on the wire too: the intent is already committed, so the second POST is
    ///     accepted and reports the same <c>Cancelling</c> run rather than answering 409.
    /// </summary>
    [Test]
    public async Task CancelRun_Repeated_Answers202Again()
    {
        var definitionId = await SeedDefinitionAsync(GraphWorkflowGraphs.StartAgentEnd);
        var runId = await StartRunAsync(definitionId);

        using var first = await SendAsync("POST", $"{Runs}/{runId}/cancel", "{}");
        using var repeat = await SendAsync("POST", $"{Runs}/{runId}/cancel", "{}");
        using var document = JsonDocument.Parse(await repeat.Content.ReadAsStringAsync());

        AssertEx.Equal(HttpStatusCode.Accepted, first.StatusCode);
        AssertEx.Equal(HttpStatusCode.Accepted, repeat.StatusCode, "the same ask answered again is not a conflict.");
        AssertEx.Equal("Cancelling", document.RootElement.GetProperty("run").GetProperty("status").GetString());
    }

    /// <summary>The run view's read: node-run summaries, and deliberately no documents on any of them.</summary>
    [Test]
    public async Task GetRun_CarriesTheNodeRunSummariesWithoutTheirDocuments()
    {
        var definitionId = await SeedDefinitionAsync(GraphWorkflowGraphs.StartAgentEnd);
        var runId = await StartRunAsync(definitionId);

        using var response = await SendAsync("GET", $"{Runs}/{runId}");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal("Pending", document.RootElement.GetProperty("run").GetProperty("status").GetString());
        var nodeRuns = document.RootElement.GetProperty("nodeRuns");
        AssertEx.Equal(expected: 3, nodeRuns.GetArrayLength());
        AssertEx.False(nodeRuns[0].TryGetProperty("output", out _), "the summaries carry no documents: they are the largest thing a run stores.");
    }

    /// <summary>
    ///     The run's own graph, and the whole reason it is on the response: the definition is edited to a DIFFERENT
    ///     graph after the run started, and the run still answers with the one it pinned. Without this the run view
    ///     draws the definition's current graph and renders every run started before an edit as node runs alone.
    /// </summary>
    [Test]
    public async Task GetRun_CarriesTheGraphTheRunPinned_EvenAfterTheDefinitionIsEditedToAnother()
    {
        var definitionId = await SeedDefinitionAsync(GraphWorkflowGraphs.StartAgentEnd);
        var runId = await StartRunAsync(definitionId);
        await ReplaceGraphAsync(definitionId, GraphWorkflowGraphs.BranchOnJson);

        using var response = await SendAsync("GET", $"{Runs}/{runId}");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var graph = document.RootElement.GetProperty("graph");
        AssertEx.Equal("analyze, done, start", NodeKeys(graph), "the run answers with the graph it started on, not with the definition's current one.");
        AssertEx.Equal(expected: 1, graph.GetProperty("schemaVersion").GetInt32());

        using var definition = JsonDocument.Parse(await (await SendAsync("GET", $"{Root}/definitions/{definitionId}")).Content
            .ReadAsStringAsync());
        AssertEx.Equal("analyze, check, done, review, ship, start",
            NodeKeys(definition.RootElement.GetProperty("graph")),
            "the definition really did move on, so the two reads are answering about different graphs.");
    }

    /// <summary>
    ///     The pinned graph is the same wire shape a definition read carries, so a client parses both with one piece of
    ///     code — per-kind config included, which is what a run view needs to label a node.
    /// </summary>
    [Test]
    public async Task GetRun_CarriesThePinnedGraphInTheSameShapeADefinitionReadDoes()
    {
        var definitionId = await SeedDefinitionAsync(GraphWorkflowGraphs.StartAgentEnd);
        var runId = await StartRunAsync(definitionId);

        using var run = JsonDocument.Parse(await (await SendAsync("GET", $"{Runs}/{runId}")).Content.ReadAsStringAsync());
        using var definition = JsonDocument.Parse(await (await SendAsync("GET", $"{Root}/definitions/{definitionId}")).Content
            .ReadAsStringAsync());

        // Structural, not raw text: what matters is that the two documents SAY the same thing, and property order is
        // the serializer's business rather than the contract's.
        AssertEx.True(JsonNode.DeepEquals(JsonNode.Parse(run.RootElement.GetProperty("graph").GetRawText()),
                JsonNode.Parse(definition.RootElement.GetProperty("graph").GetRawText())),
            $"the run's pinned graph must read as the definition's: {run.RootElement.GetProperty("graph").GetRawText()}");
        AssertEx.False(run.RootElement.GetProperty("run").TryGetProperty("graph", out _),
            "the graph sits beside the run SUMMARY rather than on it, so the run list still carries none.");
    }

    /// <summary>A graph the validator only warns about still saves and still starts — a warning blocks nothing.</summary>
    [Test]
    public async Task StartRun_OnADefinitionTheValidatorOnlyWarnsAbout_Answers202()
    {
        var definitionId = await SeedDefinitionAsync(GraphWorkflowGraphs.PauseBetweenTwoAgents);

        var runId = await StartRunAsync(definitionId);

        AssertEx.NotEqual(Guid.Empty, runId);
    }

    [Test]
    public async Task GetNodeRun_CarriesTheDocumentsAsRawJson()
    {
        var definitionId = await SeedDefinitionAsync(GraphWorkflowGraphs.StartAgentEnd);
        var runId = await StartRunAsync(definitionId);

        using var response = await SendAsync("GET", $"{Runs}/{runId}/nodes/analyze");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal("analyze", document.RootElement.GetProperty("nodeKey").GetString());
        AssertEx.Equal("Agent", document.RootElement.GetProperty("kind").GetString());
        AssertEx.True(document.RootElement.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Null,
            "a node run that has not executed has no output document, and says so as null rather than by omission.");
    }

    [Test]
    public async Task GetNodeRun_ForANodeTheRunDoesNotHave_Answers404()
    {
        var definitionId = await SeedDefinitionAsync(GraphWorkflowGraphs.StartAgentEnd);
        var runId = await StartRunAsync(definitionId);

        using var response = await SendAsync("GET", $"{Runs}/{runId}/nodes/nosuchnode");

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Test]
    public async Task ListRunEvents_PagesFromTheWatermarkAndReportsTruncation()
    {
        var definitionId = await SeedDefinitionAsync(GraphWorkflowGraphs.StartAgentEnd);
        var runId = await StartRunAsync(definitionId);

        using var response = await SendAsync("GET", $"{Runs}/{runId}/events?afterSeq=0");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var events = document.RootElement.GetProperty("events");
        AssertEx.Equal(expected: 1, events.GetArrayLength(), "a started run has written exactly its run.created event.");
        AssertEx.Equal("run.created", events[0].GetProperty("eventType").GetString());
        AssertEx.False(document.RootElement.GetProperty("replayTruncated").GetBoolean(), "one event under the cap is not a truncated page.");
        AssertEx.Equal(events[0].GetProperty("seq").GetInt64(), document.RootElement.GetProperty("lastSeq").GetInt64());

        using var past = await SendAsync("GET", $"{Runs}/{runId}/events?afterSeq={document.RootElement.GetProperty("lastSeq").GetInt64()}");
        using var empty = JsonDocument.Parse(await past.Content.ReadAsStringAsync());
        AssertEx.Equal(expected: 0, empty.RootElement.GetProperty("events").GetArrayLength(), "the watermark is exclusive.");
    }

    [Test]
    public async Task ListRunEvents_WithANegativeWatermark_Answers400()
    {
        var definitionId = await SeedDefinitionAsync(GraphWorkflowGraphs.StartAgentEnd);
        var runId = await StartRunAsync(definitionId);

        using var response = await SendAsync("GET", $"{Runs}/{runId}/events?afterSeq=-1");

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Test]
    public async Task ListRuns_WithAStatusThatIsNotAMemberName_Answers400()
    {
        using var response = await SendAsync("GET", $"{Runs}?status=nosuchstatus");

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    ///     The list finds the run this test started. Filtered to its own id rather than counted, because the host is
    ///     shared and a sibling's run is a legitimate row on the same page.
    /// </summary>
    [Test]
    public async Task ListRuns_CarriesTheRunsThisNodeHasStarted()
    {
        var definitionId = await SeedDefinitionAsync(GraphWorkflowGraphs.StartAgentEnd);
        var runId = await StartRunAsync(definitionId);

        using var response = await SendAsync("GET", $"{Runs}?status=Pending&limit=200");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Contains(document.RootElement.GetProperty("runs").EnumerateArray().Select(static run => run.GetProperty("id").GetGuid()), runId);
    }

    /// <summary>The node keys of a wire graph, sorted, which is the cheapest way to say WHICH graph came back.</summary>
    private static string NodeKeys(JsonElement graph) =>
        string.Join(", ",
            graph.GetProperty("nodes")
                 .EnumerateArray()
                 .Select(static node => node.GetProperty("key").GetString() ?? string.Empty)
                 .Order(StringComparer.Ordinal));

    /// <summary>Edits the definition to a different graph, which is what makes the run's pinned copy observable.</summary>
    private async Task ReplaceGraphAsync(Guid definitionId, string graphJson)
    {
        await using var scope = Host.Factory.Services.CreateAsyncScope();
        var current = await scope.ServiceProvider.GetRequiredService<IGraphWorkflowStore>().GetDefinitionAsync(definitionId);
        _ = await scope.ServiceProvider.GetRequiredService<IGraphWorkflowDefinitionService>()
                       .UpdateAsync(definitionId, current.Version, name: null, description: null, graphJson);
    }

    private async Task<Guid> SeedDefinitionAsync(string graphJson)
    {
        await using var scope = Host.Factory.Services.CreateAsyncScope();
        var definitions = scope.ServiceProvider.GetRequiredService<IGraphWorkflowDefinitionService>();
        var created = await definitions.CreateAsync($"Seeded {Guid.NewGuid():N}", description: null, graphJson);
        return created.Id;
    }

    private async Task<Guid> StartRunAsync(Guid definitionId)
    {
        var body = JsonSerializer.Serialize(new
        {
            requestId = Guid.NewGuid()
        });
        using var response = await SendAsync("POST", $"{Root}/definitions/{definitionId}/runs", body);
        AssertEx.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return await RunIdAsync(response);
    }

    private static async Task<Guid> RunIdAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("runId").GetGuid();
    }

    /// <summary>
    ///     The fourth capped route. Start carries an input rather than a graph, and its cap was the one with no test at
    ///     all — so the shape it answers with was unpinned on the route where the early exit runs BEFORE the definition
    ///     lookup, which is what makes an unknown definition id answer 413 here rather than 404.
    /// </summary>
    [Test]
    public async Task StartRun_WithABodyOverTheCap_Returns413InTheDeclaredShape()
    {
        using var response = await SendAsync("POST", DefinitionRuns, OversizedStartBody());

        AssertEx.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode, "start must refuse a body over the cap before it looks the definition up.");
        await RequestBodyTooLargeAssert.DeclaredProblemShapeAsync(response, $"POST {DefinitionRuns}");
    }

    /// <summary>A body whose bulk is in the run INPUT, the member this route's cap exists to bound.</summary>
    private static string OversizedStartBody()
    {
        var input = new string('a', (int)GraphWorkflowRequestSizeLimit.MaxBytes);
        return $$"""{"requestId":"44444444-4444-4444-4444-444444444444","input":"{{input}}"}""";
    }

    private static async Task<string?> ConflictTypeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("conflictType").GetString();
    }

    private async Task<HttpResponseMessage> SendAsync(string method, string route, string? body = null)
    {
        using var client = Host.Factory.CreateClient();
        using var request = Request(method, route, body);
        Host.Factory.AddNodeBearerToken(request);
        return await client.SendAsync(request);
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
}

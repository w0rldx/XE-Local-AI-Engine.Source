namespace XE_Local_AI_Engine.Tests.GraphWorkflows;

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Hubs;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The slice's gate: one whole run of <c>Start → Pause → Tool → End</c> over the WIRED host, driven through the
///     REST routes an operator's browser actually calls.
///     <para>
///         Nothing is faked. No model and no <c>FakeOllama</c>, because the graph has no <c>Agent</c> node; no scripted
///         invocation service, because <c>GetCurrentTime</c> is a real built-in that needs no flag, no workspace and no
///         model — which is exactly why it is the tool this graph names. What the other suites prove one seam at a time,
///         this proves once end to end: the definition a person saved, the run they started, the pause they answered,
///         the tool that answer let run, and the result that reached the End node.
///     </para>
///     <para>
///         Ticks come from <see cref="GraphWorkflowHarness" /> on the same host. The test server strips every hosted
///         service, so the dispatcher loop never runs and a run advances only because this test advanced it — polling
///         a status over HTTP without ticking would wait forever rather than briefly. Every wait is therefore bounded
///         by ticks, and every tick is a real round trip through the store, which is what gives the detached tool call
///         its chance to land.
///     </para>
/// </summary>
public sealed class GraphWorkflowPauseToolEndToEndTests
{
    private const string Root = "/api/local/v1/graph-workflows";

    [ClassDataSource<GraphWorkflowHostFixture>(Shared = SharedType.PerClass)]
    public required GraphWorkflowHostFixture Host { get; init; }

    /// <summary>
    ///     The whole approving path. The pause parks, announces itself, takes one answer and replays it for the second
    ///     ask, and the branch that answer opened runs a real tool whose text reaches the End node's own result.
    /// </summary>
    [Test]
    public async Task ApprovingThePause_RunsTheToolAndCarriesItsAnswerToTheEnd()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await StartRunAsync().ConfigureAwait(false);

        await AdvanceUntilRunStatusAsync(harness, runId, "WaitingForApproval").ConfigureAwait(false);

        var waiting = await EventTypesAsync(runId).ConfigureAwait(false);
        AssertEx.Contains(waiting, GraphWorkflowEventTypes.GateRequested, message: "a parked pause asks for a person by name.");
        AssertEx.Contains(waiting, GraphWorkflowEventTypes.RunWaiting, message: "and the run says it is waiting, so a list view can show it without opening it.");
        using (var parked = await RunAsync(runId).ConfigureAwait(false))
        {
            AssertEx.Equal("WaitingForApproval", NodeRunSummary(parked, "review").GetProperty("status").GetString());
        }

        var body = DecisionBody(Guid.NewGuid(), "Approve", comment: "go ahead");
        using var first = await SendAsync("POST", DecideRoute(runId, "review"), body).ConfigureAwait(false);
        var firstBody = await first.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var repeat = await SendAsync("POST", DecideRoute(runId, "review"), body).ConfigureAwait(false);
        var repeatBody = await repeat.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, first.StatusCode, firstBody);
        AssertEx.Equal(HttpStatusCode.OK, repeat.StatusCode, "a byte-identical repeat is the same act answered again, not a conflict.");
        AssertEx.Equal(firstBody, repeatBody, "the caller-minted operation id replays the answer it already recorded, verbatim.");
        using (var decision = JsonDocument.Parse(firstBody))
        {
            AssertEx.Equal("Approve", decision.RootElement.GetProperty("decision").GetString());
            AssertEx.Equal("Succeeded", decision.RootElement.GetProperty("nodeRunStatus").GetString(), "an answered pause is a node run that finished its work.");
            AssertEx.Equal("Running", decision.RootElement.GetProperty("runStatus").GetString(), "the run leaves its human wait the moment the answer commits.");
        }

        AssertEx.Equal(expected: 1,
            (await EventTypesAsync(runId).ConfigureAwait(false)).Count(type => string.Equals(type, GraphWorkflowEventTypes.GateDecided, StringComparison.Ordinal)),
            "the replay wrote nothing: one human act, one gate.decided.");

        await AdvanceUntilRunStatusAsync(harness, runId, "Completed").ConfigureAwait(false);

        using var lookup = await NodeRunAsync(runId, "lookup").ConfigureAwait(false);
        var result = AssertEx.NotNull(lookup.RootElement.GetProperty("output").GetProperty("output").GetProperty("result").GetString(),
            "the Tool node's answer lands as a string under output.result.");
        AssertEx.NotEmpty(result, "and a tool that answered nothing at all would be an empty one.");
        AssertEx.Contains(result, "UTC time:", message: "and it is the real built-in's own text, not a fake's.");

        using var done = await NodeRunAsync(runId, "done").ConfigureAwait(false);
        AssertEx.Equal(result,
            done.RootElement.GetProperty("output")
                .GetProperty("output")
                .GetProperty("result")
                .GetProperty("input")
                .GetProperty("output")
                .GetProperty("result")
                .GetString(),
            "the End node's result is its input document, so the tool's answer is what the run finished carrying.");

        using var run = await RunAsync(runId).ConfigureAwait(false);
        AssertEx.Equal("Completed", run.RootElement.GetProperty("run").GetProperty("status").GetString());
        AssertSucceededInOrder(run, "start", "review", "lookup", "done");
    }

    /// <summary>
    ///     The other answer, over the other edge. The tool node is not merely unexecuted but <c>Skipped</c>: the branch
    ///     it sat on is dead, and a run that ends without it still ends <c>Completed</c>.
    /// </summary>
    [Test]
    public async Task RejectingThePause_TakesTheRejectEdgeAndSkipsTheTool()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await StartRunAsync().ConfigureAwait(false);

        await AdvanceUntilRunStatusAsync(harness, runId, "WaitingForApproval").ConfigureAwait(false);

        using var decided = await SendAsync("POST", DecideRoute(runId, "review"), DecisionBody(Guid.NewGuid(), "Reject")).ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.OK, decided.StatusCode, await decided.Content.ReadAsStringAsync().ConfigureAwait(false));

        await AdvanceUntilRunStatusAsync(harness, runId, "Completed").ConfigureAwait(false);

        using var run = await RunAsync(runId).ConfigureAwait(false);
        AssertEx.Equal("Succeeded", NodeRunSummary(run, "review").GetProperty("status").GetString());
        AssertEx.Equal("Skipped", NodeRunSummary(run, "lookup").GetProperty("status").GetString(), "the approving edge is dead, so the tool never runs at all.");
        AssertEx.Equal("Succeeded", NodeRunSummary(run, "done").GetProperty("status").GetString(), "and the reject edge still reaches the End node.");
    }

    /// <summary>
    ///     One assertion tying the picker to the run: the tool this graph names is a tool the picker offers, from the
    ///     same host. A feed that stopped listing it would leave an author unable to author the graph above.
    /// </summary>
    [Test]
    public async Task ThePickerFeed_OffersTheToolThisGraphInvokes()
    {
        using var response = await SendAsync("GET", $"{Root}/tools", body: null).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode, body);
        using var document = JsonDocument.Parse(body);
        AssertEx.Contains(document.RootElement.GetProperty("tools").EnumerateArray().Select(static tool => tool.GetProperty("name").GetString()),
            "GetCurrentTime",
            "the graph this suite runs names a tool the picker must be able to offer.");
    }

    /// <summary>
    ///     A real subscriber over the real hub, and the one push with a consequence beyond repainting: a <c>gate</c>
    ///     arrives when the pause parks and again when it is answered, so a watching client learns that a person is
    ///     wanted and that they are no longer wanted without polling for either.
    /// </summary>
    [Test]
    public async Task TheHub_AnnouncesTheGateWhenItParksAndAgainWhenItIsDecided()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await StartRunAsync().ConfigureAwait(false);

        var gates = new ConcurrentQueue<GraphWorkflowChanged>();
        await using var connection = new HubConnectionBuilder()
                                     .WithUrl("http://localhost" + LocalApiRoutes.GraphWorkflows.Hub, options =>
                                     {
                                         options.HttpMessageHandlerFactory = _ => Host.Factory.Server.CreateHandler();
                                         options.AccessTokenProvider = () => Task.FromResult<string?>(Host.Factory.CreateNodeAccessToken());
                                         options.Headers.Add("Origin", "http://localhost");
                                     })
                                     .Build();
        _ = connection.On<GraphWorkflowChanged>(GraphWorkflowHubEvents.Changed, changed =>
        {
            // A shared host publishes every sibling's run into its own group, and this connection joined only one of
            // them — but the run id is filtered anyway, because a group is not an assertion.
            if (changed.RunId == runId && string.Equals(changed.Kind, "gate", StringComparison.Ordinal))
            {
                gates.Enqueue(changed);
            }
        });

        await connection.StartAsync().ConfigureAwait(false);
        var snapshot = await connection.InvokeAsync<GraphWorkflowRunSubscriptionSnapshot>("SubscribeRun", runId, 0L).ConfigureAwait(false);
        AssertEx.Equal(expected: 0, snapshot.PendingDecisionCount, "the run has not reached its pause yet, so nobody is being asked for anything.");

        await AdvanceUntilRunStatusAsync(harness, runId, "WaitingForApproval").ConfigureAwait(false);
        await AssertEx.EventuallyAsync(() => !gates.IsEmpty, TestBudgets.Contended, "parking on a pause must announce a gate to whoever is watching.").ConfigureAwait(false);

        using var decided = await SendAsync("POST", DecideRoute(runId, "review"), DecisionBody(Guid.NewGuid(), "Approve")).ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.OK, decided.StatusCode, await decided.Content.ReadAsStringAsync().ConfigureAwait(false));

        await AssertEx.EventuallyAsync(() => gates.Count >= 2, TestBudgets.Contended, "and answering it must announce a second one: that is what clears the badge.")
                      .ConfigureAwait(false);
    }

    /// <summary>A definition saved and a run started, both through the routes the SPA calls.</summary>
    private async Task<Guid> StartRunAsync()
    {
        using var created = await SendAsync("POST",
                                $"{Root}/definitions",
                                $$"""{"name":"Pause then tool {{Guid.NewGuid():N}}","description":"The slice gate.","graph":{{GraphWorkflowGraphs.PauseThenToolEndToEnd}}}""")
                                .ConfigureAwait(false);
        var createdBody = await created.Content.ReadAsStringAsync().ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.Created, created.StatusCode, createdBody);
        using var definition = JsonDocument.Parse(createdBody);
        var definitionId = definition.RootElement.GetProperty("id").GetGuid();

        using var started = await SendAsync("POST",
                                $"{Root}/definitions/{definitionId}/runs",
                                $$"""{"requestId":"{{Guid.NewGuid()}}"}""")
                                .ConfigureAwait(false);
        var startedBody = await started.Content.ReadAsStringAsync().ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.Accepted, started.StatusCode, startedBody);
        using var run = JsonDocument.Parse(startedBody);
        return run.RootElement.GetProperty("runId").GetGuid();
    }

    /// <summary>
    ///     Ticks until <c>GET run</c> reports <paramref name="status" />, which is the status a client would see.
    ///     <para>
    ///         Bounded by ticks rather than by a clock: on this host nothing advances a run but this call, so a
    ///         wall-clock wait would measure nothing and a sleep would prove nothing.
    ///     </para>
    /// </summary>
    private async Task AdvanceUntilRunStatusAsync(GraphWorkflowHarness harness, Guid runId, string status) =>
        await harness.AdvanceUntilAsync(runId,
                         async () =>
                         {
                             using var run = await RunAsync(runId).ConfigureAwait(false);
                             return string.Equals(run.RootElement.GetProperty("run").GetProperty("status").GetString(), status, StringComparison.Ordinal);
                         },
                         $"Run {runId} never reached {status} over the wire")
                     .ConfigureAwait(false);

    /// <summary>Every named node succeeded, and finished no earlier than the one before it.</summary>
    private static void AssertSucceededInOrder(JsonDocument run, params string[] nodeKeys)
    {
        long previous = 0;
        foreach (var nodeKey in nodeKeys)
        {
            var summary = NodeRunSummary(run, nodeKey);
            AssertEx.Equal("Succeeded", summary.GetProperty("status").GetString(), $"'{nodeKey}' was expected to have succeeded.");
            var completedAtUtc = summary.GetProperty("completedAtUtc").GetInt64();
            AssertEx.True(completedAtUtc >= previous, $"'{nodeKey}' finished before the node it waited on; the run did not execute in graph order.");
            previous = completedAtUtc;
        }
    }

    private static JsonElement NodeRunSummary(JsonDocument run, string nodeKey) =>
        run.RootElement.GetProperty("nodeRuns")
           .EnumerateArray()
           .SingleOrDefault(nodeRun => string.Equals(nodeRun.GetProperty("nodeKey").GetString(), nodeKey, StringComparison.Ordinal))
           is { ValueKind: JsonValueKind.Object } summary
            ? summary
            : throw new AssertionException($"The run carries no node run for '{nodeKey}'.");

    private async Task<JsonDocument> RunAsync(Guid runId) =>
        await ReadJsonAsync("GET", $"{Root}/runs/{runId}").ConfigureAwait(false);

    private async Task<JsonDocument> NodeRunAsync(Guid runId, string nodeKey) =>
        await ReadJsonAsync("GET", $"{Root}/runs/{runId}/nodes/{nodeKey}").ConfigureAwait(false);

    private async Task<IReadOnlyList<string>> EventTypesAsync(Guid runId)
    {
        using var document = await ReadJsonAsync("GET", $"{Root}/runs/{runId}/events?afterSeq=0").ConfigureAwait(false);
        return [.. document.RootElement.GetProperty("events").EnumerateArray().Select(static entry => entry.GetProperty("eventType").GetString() ?? string.Empty)];
    }

    private async Task<JsonDocument> ReadJsonAsync(string method, string route)
    {
        using var response = await SendAsync(method, route, body: null).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode, $"{method} {route} answered {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body);
    }

    private static string DecideRoute(Guid runId, string nodeKey) =>
        $"{Root}/runs/{runId}/nodes/{nodeKey}/decide";

    private static string DecisionBody(Guid operationId, string decision, string? comment = null) =>
        JsonSerializer.Serialize(new
        {
            operationId,
            decision,
            comment
        });

    private async Task<HttpResponseMessage> SendAsync(string method, string route, string? body)
    {
        using var client = Host.Factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), route);
        if (method is "POST")
        {
            request.Content = new StringContent(body ?? "{}", Encoding.UTF8, "application/json");
        }

        Host.Factory.AddNodeBearerToken(request);
        return await client.SendAsync(request).ConfigureAwait(false);
    }
}

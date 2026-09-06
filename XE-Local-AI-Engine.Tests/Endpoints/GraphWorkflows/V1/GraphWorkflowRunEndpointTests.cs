namespace XE_Local_AI_Engine.Tests.Endpoints.GraphWorkflows.V1;

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
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

        using var response = await client.SendAsync(request).ConfigureAwait(false);

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

        using var response = await client.SendAsync(request).ConfigureAwait(false);

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

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode, $"{method} {route} must answer 404 on a disabled node, never 500.");
    }

    /// <summary>
    ///     202, not 200: the endpoint commits a durable intent and the dispatcher advances it out of band. The same
    ///     request id answers the same run id, which is the whole point of a caller-minted key.
    /// </summary>
    [Test]
    public async Task StartRun_Answers202WithTheRunId_AndARetryAnswersTheSameOne()
    {
        var definitionId = await SeedDefinitionAsync(GraphWorkflowGraphs.StartAgentEnd).ConfigureAwait(false);
        var requestId = Guid.NewGuid();
        var body = JsonSerializer.Serialize(new
        {
            requestId,
            input = new
            {
                topic = "latency"
            }
        });

        using var first = await SendAsync("POST", $"{Root}/definitions/{definitionId}/runs", body).ConfigureAwait(false);
        using var second = await SendAsync("POST", $"{Root}/definitions/{definitionId}/runs", body).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Accepted, first.StatusCode);
        AssertEx.Equal(HttpStatusCode.Accepted, second.StatusCode);
        var firstRunId = await RunIdAsync(first).ConfigureAwait(false);
        AssertEx.Equal(firstRunId, await RunIdAsync(second).ConfigureAwait(false), "the same request id resolves to the run it already started.");
        AssertEx.NotEqual(Guid.Empty, firstRunId);
    }

    [Test]
    public async Task StartRun_WithNoRequestId_Answers400()
    {
        var definitionId = await SeedDefinitionAsync(GraphWorkflowGraphs.StartAgentEnd).ConfigureAwait(false);

        using var response = await SendAsync("POST", $"{Root}/definitions/{definitionId}/runs", "{}").ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode, "an unminted idempotency key is a caller mistake, not a conflict.");
    }

    /// <summary>
    ///     A stale <c>definitionVersion</c> is one of the two ways a run command loses, and it reaches the client as the
    ///     409 discriminator the SPA branches on.
    /// </summary>
    [Test]
    public async Task StartRun_WithAStaleDefinitionVersion_Answers409WithTheRunConflictDiscriminator()
    {
        var definitionId = await SeedDefinitionAsync(GraphWorkflowGraphs.StartAgentEnd).ConfigureAwait(false);
        var body = JsonSerializer.Serialize(new
        {
            requestId = Guid.NewGuid(),
            definitionVersion = 99
        });

        using var response = await SendAsync("POST", $"{Root}/definitions/{definitionId}/runs", body).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        AssertEx.Equal("GraphWorkflowRunConflict", await ConflictTypeAsync(response).ConfigureAwait(false));
    }

    /// <summary>The other way: a cancel of a run that has already finished. Same story, same discriminator.</summary>
    [Test]
    public async Task CancelRun_OnATerminalRun_Answers409WithTheRunConflictDiscriminator()
    {
        var definitionId = await SeedDefinitionAsync(GraphWorkflowGraphs.StartAgentEnd).ConfigureAwait(false);
        var runId = await StartRunAsync(definitionId).ConfigureAwait(false);

        // Driven terminal through the store: there is no dispatcher in this slice to finish a run on its own.
        await using (var scope = Host.Factory.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IGraphWorkflowStore>();
            var run = await store.GetRunAsync(runId).ConfigureAwait(false);
            _ = await store.TransitionRunAsync(new TransitionGraphWorkflowRunCommand(runId,
                               run.Version,
                               GraphWorkflowRunStatus.Failed,
                               GraphWorkflowFailureClass.NodeFailed))
                           .ConfigureAwait(false);
        }

        using var response = await SendAsync("POST", $"{Runs}/{runId}/cancel", "{}").ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        AssertEx.Equal("GraphWorkflowRunConflict", await ConflictTypeAsync(response).ConfigureAwait(false));
    }

    [Test]
    public async Task CancelRun_OnALiveRun_Answers202AndTheBodyReadsCancelling()
    {
        var definitionId = await SeedDefinitionAsync(GraphWorkflowGraphs.StartAgentEnd).ConfigureAwait(false);
        var runId = await StartRunAsync(definitionId).ConfigureAwait(false);

        using var response = await SendAsync("POST", $"{Runs}/{runId}/cancel", "{}").ConfigureAwait(false);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));

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
        var definitionId = await SeedDefinitionAsync(GraphWorkflowGraphs.StartAgentEnd).ConfigureAwait(false);
        var runId = await StartRunAsync(definitionId).ConfigureAwait(false);

        using var first = await SendAsync("POST", $"{Runs}/{runId}/cancel", "{}").ConfigureAwait(false);
        using var repeat = await SendAsync("POST", $"{Runs}/{runId}/cancel", "{}").ConfigureAwait(false);
        using var document = JsonDocument.Parse(await repeat.Content.ReadAsStringAsync().ConfigureAwait(false));

        AssertEx.Equal(HttpStatusCode.Accepted, first.StatusCode);
        AssertEx.Equal(HttpStatusCode.Accepted, repeat.StatusCode, "the same ask answered again is not a conflict.");
        AssertEx.Equal("Cancelling", document.RootElement.GetProperty("run").GetProperty("status").GetString());
    }

    /// <summary>The run view's read: node-run summaries, and deliberately no documents on any of them.</summary>
    [Test]
    public async Task GetRun_CarriesTheNodeRunSummariesWithoutTheirDocuments()
    {
        var definitionId = await SeedDefinitionAsync(GraphWorkflowGraphs.StartAgentEnd).ConfigureAwait(false);
        var runId = await StartRunAsync(definitionId).ConfigureAwait(false);

        using var response = await SendAsync("GET", $"{Runs}/{runId}").ConfigureAwait(false);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal("Pending", document.RootElement.GetProperty("run").GetProperty("status").GetString());
        var nodeRuns = document.RootElement.GetProperty("nodeRuns");
        AssertEx.Equal(expected: 3, nodeRuns.GetArrayLength());
        AssertEx.False(nodeRuns[0].TryGetProperty("output", out _), "the summaries carry no documents: they are the largest thing a run stores.");
    }

    [Test]
    public async Task GetNodeRun_CarriesTheDocumentsAsRawJson()
    {
        var definitionId = await SeedDefinitionAsync(GraphWorkflowGraphs.StartAgentEnd).ConfigureAwait(false);
        var runId = await StartRunAsync(definitionId).ConfigureAwait(false);

        using var response = await SendAsync("GET", $"{Runs}/{runId}/nodes/analyze").ConfigureAwait(false);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal("analyze", document.RootElement.GetProperty("nodeKey").GetString());
        AssertEx.Equal("Agent", document.RootElement.GetProperty("kind").GetString());
        AssertEx.True(document.RootElement.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Null,
            "a node run that has not executed has no output document, and says so as null rather than by omission.");
    }

    [Test]
    public async Task GetNodeRun_ForANodeTheRunDoesNotHave_Answers404()
    {
        var definitionId = await SeedDefinitionAsync(GraphWorkflowGraphs.StartAgentEnd).ConfigureAwait(false);
        var runId = await StartRunAsync(definitionId).ConfigureAwait(false);

        using var response = await SendAsync("GET", $"{Runs}/{runId}/nodes/nosuchnode").ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Test]
    public async Task ListRunEvents_PagesFromTheWatermarkAndReportsTruncation()
    {
        var definitionId = await SeedDefinitionAsync(GraphWorkflowGraphs.StartAgentEnd).ConfigureAwait(false);
        var runId = await StartRunAsync(definitionId).ConfigureAwait(false);

        using var response = await SendAsync("GET", $"{Runs}/{runId}/events?afterSeq=0").ConfigureAwait(false);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var events = document.RootElement.GetProperty("events");
        AssertEx.Equal(expected: 1, events.GetArrayLength(), "a started run has written exactly its run.created event.");
        AssertEx.Equal("run.created", events[0].GetProperty("eventType").GetString());
        AssertEx.False(document.RootElement.GetProperty("replayTruncated").GetBoolean(), "one event under the cap is not a truncated page.");
        AssertEx.Equal(events[0].GetProperty("seq").GetInt64(), document.RootElement.GetProperty("lastSeq").GetInt64());

        using var past = await SendAsync("GET", $"{Runs}/{runId}/events?afterSeq={document.RootElement.GetProperty("lastSeq").GetInt64()}").ConfigureAwait(false);
        using var empty = JsonDocument.Parse(await past.Content.ReadAsStringAsync().ConfigureAwait(false));
        AssertEx.Equal(expected: 0, empty.RootElement.GetProperty("events").GetArrayLength(), "the watermark is exclusive.");
    }

    [Test]
    public async Task ListRunEvents_WithANegativeWatermark_Answers400()
    {
        var definitionId = await SeedDefinitionAsync(GraphWorkflowGraphs.StartAgentEnd).ConfigureAwait(false);
        var runId = await StartRunAsync(definitionId).ConfigureAwait(false);

        using var response = await SendAsync("GET", $"{Runs}/{runId}/events?afterSeq=-1").ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Test]
    public async Task ListRuns_WithAStatusThatIsNotAMemberName_Answers400()
    {
        using var response = await SendAsync("GET", $"{Runs}?status=nosuchstatus").ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    ///     The list finds the run this test started. Filtered to its own id rather than counted, because the host is
    ///     shared and a sibling's run is a legitimate row on the same page.
    /// </summary>
    [Test]
    public async Task ListRuns_CarriesTheRunsThisNodeHasStarted()
    {
        var definitionId = await SeedDefinitionAsync(GraphWorkflowGraphs.StartAgentEnd).ConfigureAwait(false);
        var runId = await StartRunAsync(definitionId).ConfigureAwait(false);

        using var response = await SendAsync("GET", $"{Runs}?status=Pending&limit=200").ConfigureAwait(false);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Contains(document.RootElement.GetProperty("runs").EnumerateArray().Select(static run => run.GetProperty("id").GetGuid()), runId);
    }

    private async Task<Guid> SeedDefinitionAsync(string graphJson)
    {
        await using var scope = Host.Factory.Services.CreateAsyncScope();
        var definitions = scope.ServiceProvider.GetRequiredService<IGraphWorkflowDefinitionService>();
        var created = await definitions.CreateAsync($"Seeded {Guid.NewGuid():N}", description: null, graphJson).ConfigureAwait(false);
        return created.Id;
    }

    private async Task<Guid> StartRunAsync(Guid definitionId)
    {
        var body = JsonSerializer.Serialize(new
        {
            requestId = Guid.NewGuid()
        });
        using var response = await SendAsync("POST", $"{Root}/definitions/{definitionId}/runs", body).ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return await RunIdAsync(response).ConfigureAwait(false);
    }

    private static async Task<Guid> RunIdAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        return document.RootElement.GetProperty("runId").GetGuid();
    }

    private static async Task<string?> ConflictTypeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        return document.RootElement.GetProperty("conflictType").GetString();
    }

    private async Task<HttpResponseMessage> SendAsync(string method, string route, string? body = null)
    {
        using var client = Host.Factory.CreateClient();
        using var request = Request(method, route, body);
        Host.Factory.AddNodeBearerToken(request);
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
}

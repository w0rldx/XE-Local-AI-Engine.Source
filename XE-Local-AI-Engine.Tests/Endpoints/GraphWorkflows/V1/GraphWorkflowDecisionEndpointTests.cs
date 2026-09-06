namespace XE_Local_AI_Engine.Tests.Endpoints.GraphWorkflows.V1;

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Tests.GraphWorkflows;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The decide route, over the wired host and the real store.
///     <para>
///         This is where every one of the runtime's refusals is asserted as the client actually sees it — the
///         <c>conflictType</c> discriminator the SPA branches on and the <c>standingDecision</c> beside it. A missing
///         <c>ConflictExceptionHandler</c> arm reads as a failure here rather than as a 500 in production, which is
///         the whole reason these live at the wire and not beside the service tests.
///     </para>
///     <para>
///         Ticks are driven through <see cref="GraphWorkflowHarness" /> on the same host: the test server strips every
///         hosted service, so a run parks on its pause only because this test advanced it.
///     </para>
/// </summary>
public sealed class GraphWorkflowDecisionEndpointTests
{
    private const string Root = "/api/local/v1/graph-workflows";

    /// <summary>The <c>sub</c> claim on the operator token this suite sends — the node user id, not the address.</summary>
    private const string OperatorSubject = "node-admin-test";

    /// <summary>A route shape for the auth and feature-gate sweeps; the ids need not exist, because none of those reach a store.</summary>
    private const string DecideRoute = $"{Root}/runs/33333333-3333-3333-3333-333333333333/nodes/review/decide";

    [ClassDataSource<GraphWorkflowHostFixture>(Shared = SharedType.PerClass)]
    public required GraphWorkflowHostFixture Host { get; init; }

    [Test]
    public async Task Decide_WhenTheOperatorTokenIsMissing_ReturnsUnauthorized()
    {
        using var client = Host.Factory.CreateClient();
        using var request = Request(DecideRoute, Body(Guid.NewGuid(), "Approve"));

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task Decide_WithANonOperatorToken_ReturnsForbidden()
    {
        using var client = Host.Factory.CreateClient();
        using var request = Request(DecideRoute, Body(Guid.NewGuid(), "Approve"));
        Host.Factory.AddNonOperatorBearerToken(request);

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Forbidden, response.StatusCode, "answering a gate is an operator act.");
    }

    /// <summary>404 ahead of auth on a disabled node, indistinguishable from a wrong route.</summary>
    [Test]
    public async Task Decide_WhenTheFeatureIsDisabled_ReturnsNotFound()
    {
        await using var factory = new TestServerWebAppFactory
        {
            AdditionalConfiguration = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["GraphWorkflows:Enabled"] = "false"
            }
        };

        using var client = factory.CreateClient();
        using var request = Request(DecideRoute, Body(Guid.NewGuid(), "Approve"));
        factory.AddNodeBearerToken(request);

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode, "the route must answer 404 on a disabled node, never 500.");
    }

    [Test]
    public async Task Decide_OnAnUnknownRun_Answers404()
    {
        using var response = await SendAsync(DecideRoute, Body(Guid.NewGuid(), "Approve")).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Test]
    public async Task Decide_OnANodeKeyTheRunDoesNotHave_Answers404()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await ParkedRunAsync(harness).ConfigureAwait(false);

        using var response = await SendAsync(RouteFor(runId, "nosuchnode"), Body(Guid.NewGuid(), "Approve")).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Test]
    [Arguments("Maybe")]
    [Arguments("")]
    public async Task Decide_WithADecisionThatIsNotAMemberName_Answers400(string decision)
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await ParkedRunAsync(harness).ConfigureAwait(false);

        using var response = await SendAsync(RouteFor(runId, "review"), Body(Guid.NewGuid(), decision)).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode, "an unknown token is a caller mistake, and the handler must never Enum.Parse it.");
    }

    [Test]
    public async Task Decide_WithNoOperationId_Answers400()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await ParkedRunAsync(harness).ConfigureAwait(false);

        using var response = await SendAsync(RouteFor(runId, "review"), Body(Guid.Empty, "Approve")).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode, "an unminted idempotency key would replay the first caller's answer.");
    }

    /// <summary>The happy path, and the idempotency the caller-minted key promises: the same body twice, the same 200.</summary>
    [Test]
    public async Task Decide_Answers200WithTheCurrentStatuses_AndTheSameBodyTwiceAnswersTheSame()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await ParkedRunAsync(harness).ConfigureAwait(false);
        var body = Body(Guid.NewGuid(), "Approve", comment: "ship it");

        using var first = await SendAsync(RouteFor(runId, "review"), body).ConfigureAwait(false);
        using var second = await SendAsync(RouteFor(runId, "review"), body).ConfigureAwait(false);
        using var document = JsonDocument.Parse(await first.Content.ReadAsStringAsync().ConfigureAwait(false));
        using var replay = JsonDocument.Parse(await second.Content.ReadAsStringAsync().ConfigureAwait(false));

        AssertEx.Equal(HttpStatusCode.OK, first.StatusCode);
        AssertEx.Equal(HttpStatusCode.OK, second.StatusCode, "a byte-identical repeat is the same act answered again, not a conflict.");
        AssertEx.Equal("Approve", document.RootElement.GetProperty("decision").GetString());
        AssertEx.Equal("Succeeded", document.RootElement.GetProperty("nodeRunStatus").GetString());
        AssertEx.Equal("Running", document.RootElement.GetProperty("runStatus").GetString());
        AssertEx.Equal(AssertEx.NotNull(document.RootElement.GetProperty("decision").GetString()), replay.RootElement.GetProperty("decision").GetString());
    }

    /// <summary>
    ///     The second human act on an answered pause. <c>standingDecision</c> is the point: the person who clicked has
    ///     to be told what was decided, not only that their click failed.
    /// </summary>
    [Test]
    public async Task Decide_WithADifferentOperationIdOnAnAnsweredPause_Answers409WithTheStandingDecision()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await ParkedRunAsync(harness).ConfigureAwait(false);
        using var accepted = await SendAsync(RouteFor(runId, "review"), Body(Guid.NewGuid(), "Reject")).ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.OK, accepted.StatusCode);

        using var response = await SendAsync(RouteFor(runId, "review"), Body(Guid.NewGuid(), "Approve")).ConfigureAwait(false);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        AssertEx.Equal("GraphWorkflowGateAlreadyDecided", document.RootElement.GetProperty("conflictType").GetString());
        AssertEx.Equal("Reject", document.RootElement.GetProperty("standingDecision").GetString());
    }

    /// <summary>An id reused on a SECOND pause of the same run, which the run-wide lookup catches before the index does.</summary>
    [Test]
    public async Task Decide_WithAnOperationIdAlreadyUsedOnAnotherPauseOfTheRun_Answers409()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await harness.StartRunAsync(GraphWorkflowGraphs.PauseTwoPausesInSequence).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        var operationId = Guid.NewGuid();
        using var accepted = await SendAsync(RouteFor(runId, "first"), Body(operationId, "Approve")).ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.OK, accepted.StatusCode);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        using var response = await SendAsync(RouteFor(runId, "second"), Body(operationId, "Approve")).ConfigureAwait(false);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode, "not a replay, and not a DbUpdateException either.");
        AssertEx.Equal("GraphWorkflowGateAlreadyDecided", document.RootElement.GetProperty("conflictType").GetString());
    }

    /// <summary>An answer the pinned graph never offered. The graph is wrong, so it reaches the client as S1's run conflict.</summary>
    [Test]
    public async Task Decide_WithAnAnswerThePauseDoesNotOffer_Answers409WithTheRunConflictDiscriminator()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await harness.StartRunAsync(GraphWorkflowGraphs.PauseTwoPausesInSequence).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        using var response = await SendAsync(RouteFor(runId, "first"), Body(Guid.NewGuid(), "Reject")).ConfigureAwait(false);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        AssertEx.Equal("GraphWorkflowRunConflict", document.RootElement.GetProperty("conflictType").GetString());
    }

    [Test]
    public async Task Decide_WhileTheRunIsCancelling_Answers409WithTheRunConflictDiscriminator()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await ParkedRunAsync(harness).ConfigureAwait(false);
        await harness.CancelAsync(runId).ConfigureAwait(false);

        using var response = await SendAsync(RouteFor(runId, "review"), Body(Guid.NewGuid(), "Approve")).ConfigureAwait(false);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        AssertEx.Equal("GraphWorkflowRunConflict", document.RootElement.GetProperty("conflictType").GetString());
    }

    /// <summary>A body rule, and therefore a 400 rather than a conflict: it is about what was sent, not about the run.</summary>
    [Test]
    public async Task Decide_WithNoCommentOnAPauseThatRequiresOne_Answers400()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await harness.StartRunAsync(GraphWorkflowGraphs.PauseRequiringComment).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        using var response = await SendAsync(RouteFor(runId, "review"), Body(Guid.NewGuid(), "Approve")).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>The payload rides as raw JSON, and lands under the node's own output document.</summary>
    [Test]
    public async Task Decide_WithAPayload_StoresItUnderTheNodeRunOutput()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await ParkedRunAsync(harness).ConfigureAwait(false);
        var body = JsonSerializer.Serialize(new
        {
            operationId = Guid.NewGuid(),
            decision = "Approve",
            payload = new
            {
                ticket = "XE-42"
            }
        });

        using var response = await SendAsync(RouteFor(runId, "review"), body).ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        using var stored = await NodeRunAsync(runId, "review").ConfigureAwait(false);
        AssertEx.Equal("XE-42", stored.RootElement.GetProperty("output").GetProperty("output").GetProperty("payload").GetProperty("ticket").GetString());
    }

    /// <summary>
    ///     Attribution, end to end. The row has to record WHICH ACCOUNT answered — the one question a review of an
    ///     AI-driven run actually gets asked — and nothing but a real token through the real endpoint proves the claim
    ///     survives the hop. Read through the store because the node-run DTO deliberately does not publish a subject.
    /// </summary>
    [Test]
    public async Task Decide_RecordsTheDecidingSubjectFromTheOperatorToken()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await ParkedRunAsync(harness).ConfigureAwait(false);

        using var response = await SendAsync(RouteFor(runId, "review"), Body(Guid.NewGuid(), "Approve")).ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var scope = Host.Factory.Services.CreateAsyncScope();
        var decided = await scope.ServiceProvider.GetRequiredService<IGraphWorkflowStore>().GetNodeRunAsync(runId, "review").ConfigureAwait(false);
        AssertEx.Equal(OperatorSubject, decided.DecidedBySubject, "the sub claim on the operator token is what the audit trail keeps.");
    }

    /// <summary>
    ///     A pass-through node downstream of an answered pause carries that pause's <c>output.decision</c> verbatim, so
    ///     a standing decision must be read off the column an answered gate WRITES rather than off the document. Without
    ///     that, deciding a Condition or Parallel node would be refused with a decision nobody ever took on it.
    /// </summary>
    [Test]
    public async Task Decide_OnAPassThroughNodeCarryingAnUpstreamDecision_Answers409WithoutAStandingDecision()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await harness.StartRunAsync(GraphWorkflowGraphs.PauseStrandedRejection).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        using var rejected = await SendAsync(RouteFor(runId, "review"), Body(Guid.NewGuid(), "Reject")).ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.OK, rejected.StatusCode);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var passThrough = await harness.ReadNodeRunAsync(runId, "deadend").ConfigureAwait(false);
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Succeeded, passThrough.Status);
        AssertEx.Contains(passThrough.OutputJson, "\"decision\":\"Reject\"", StringComparison.Ordinal, "the pass-through really does carry the pause's answer.");

        using var response = await SendAsync(RouteFor(runId, "deadend"), Body(Guid.NewGuid(), "Approve")).ConfigureAwait(false);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        AssertEx.Equal("GraphWorkflowRunConflict", document.RootElement.GetProperty("conflictType").GetString());
        AssertEx.False(document.RootElement.TryGetProperty("standingDecision", out _), "nobody decided this node, so nothing stands on it.");
    }

    private async Task<JsonDocument> NodeRunAsync(Guid runId, string nodeKey)
    {
        using var response = await SendAsync($"{Root}/runs/{runId}/nodes/{nodeKey}", body: null, "GET").ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
    }

    private static async Task<Guid> ParkedRunAsync(GraphWorkflowHarness harness)
    {
        var runId = await harness.StartRunAsync(GraphWorkflowGraphs.PauseTwoDecisions).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        AssertEx.Equal(GraphWorkflowNodeRunStatus.WaitingForApproval,
            (await harness.ReadNodeRunAsync(runId, "review").ConfigureAwait(false)).Status,
            "the run was expected to park on its pause before the route is asked anything.");
        return runId;
    }

    private static string RouteFor(Guid runId, string nodeKey) =>
        $"{Root}/runs/{runId}/nodes/{nodeKey}/decide";

    private static string Body(Guid operationId, string decision, string? comment = null) =>
        JsonSerializer.Serialize(new
        {
            operationId,
            decision,
            comment
        });

    private async Task<HttpResponseMessage> SendAsync(string route, string? body, string method = "POST")
    {
        using var client = Host.Factory.CreateClient();
        using var request = Request(route, body, method);
        Host.Factory.AddNodeBearerToken(request);
        return await client.SendAsync(request).ConfigureAwait(false);
    }

    private static HttpRequestMessage Request(string route, string? body, string method = "POST")
    {
        var request = new HttpRequestMessage(new HttpMethod(method), route);
        if (method == "POST")
        {
            request.Content = new StringContent(body ?? "{}", Encoding.UTF8, "application/json");
        }

        return request;
    }
}

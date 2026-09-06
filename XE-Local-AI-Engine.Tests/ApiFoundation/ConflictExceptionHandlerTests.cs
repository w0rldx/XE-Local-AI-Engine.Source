namespace XE_Local_AI_Engine.Tests.ApiFoundation;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.ExceptionHandling;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Client.Services.PreviewWorkflows;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Pins the contract of <c>ConflictExceptionHandler</c> for the cap conflicts the preview execute endpoints used to
///     hand-build: one 409 <c>application/problem+json</c> envelope, discriminated by <c>conflictType</c>, carrying the
///     exception message as <c>detail</c>, a <c>traceId</c>, and the cap numbers as problem-details extensions (the
///     numbers are the whole point of the rejection — an operator cannot act on "cap reached" alone).
///     <para>
///         The other mappings are pinned where they are provoked end to end:
///         <c>ReadOnlyConversation</c> in <c>NodeChatReadOnlyEndpointTests</c>, <c>WorkspaceRevocationBusy</c> in
///         <c>WorkspaceEndpointTests</c>, the worker/image conflicts in <c>ConnectionEndpointTests</c>, and
///         <c>InstalledModelHasDependentAdapters</c> in <c>LocalModelEndpointTests</c>.
///     </para>
/// </summary>
public sealed class ConflictExceptionHandlerTests
{
    private const string ExecuteRoute = "/api/local/v1/preview/runs/execute";

    /// <summary>Start text the substituted execution service switches on, so ONE host serves both cap conflicts.</summary>
    private const string RunCapStartText = "provoke-run-cap";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Test]
    public async Task ExecuteUnsaved_WhenRunCapReached_WritesCapReachedConflictProblemDetails()
    {
        await using var factory = CreateFactory();

        var body = await ExecuteAsync(factory, RunCapStartText).ConfigureAwait(false);

        AssertEx.Equal("PreviewWorkflowCapReached", body.ConflictType);
        AssertEx.Equal("Conflict", body.Title);
        AssertEx.Equal(expected: 409, body.Status);
        AssertEx.Contains(body.Detail, "concurrent preview runs", StringComparison.Ordinal);
        AssertEx.NotEmpty(body.TraceId);
        AssertEx.Equal(expected: 3, body.MaxConcurrentRuns);
        AssertEx.Null(body.DistinctModelCount);
    }

    [Test]
    public async Task ExecuteUnsaved_WhenModelCapExceeded_WritesModelCapConflictProblemDetails()
    {
        await using var factory = CreateFactory();

        var body = await ExecuteAsync(factory, "provoke-model-cap").ConfigureAwait(false);

        AssertEx.Equal("PreviewWorkflowModelCapExceeded", body.ConflictType);
        AssertEx.Equal(expected: 409, body.Status);
        AssertEx.NotEmpty(body.TraceId);
        AssertEx.Equal(expected: 5, body.DistinctModelCount);
        AssertEx.Equal(expected: 2, body.MaxLoadedProcesses);
        AssertEx.Null(body.MaxConcurrentRuns);
    }

    /// <summary>
    ///     The graph-workflow run family's arm, asserted against the handler itself rather than over a route. The three
    ///     exception types it maps come from three different layers, and one of them — the store's own rejection
    ///     channel — is not reachable from any endpoint without first winning a race; asking the switch directly is the
    ///     only way to pin all three without staging one.
    /// </summary>
    [Test]
    [Arguments("run conflict")]
    [Arguments("invalid transition")]
    public async Task TryHandleAsync_ForAGraphWorkflowRunFailure_WritesTheRunConflictDiscriminator(string kind)
    {
        Exception exception = kind == "run conflict"
            ? new GraphWorkflowRunConflictException("This run is already Failed, so there is nothing to cancel.")
            : new GraphWorkflowInvalidTransitionException("The graph workflow run version is stale (expected 3, current 4).");

        var body = await HandleAsync(exception).ConfigureAwait(false);

        AssertEx.Equal("GraphWorkflowRunConflict", body.ConflictType, "both reach a client as one instruction: re-read the run.");
        AssertEx.Equal(expected: 409, body.Status);
        AssertEx.Equal(exception.Message, body.Detail);
        AssertEx.NotEmpty(body.TraceId);
    }

    /// <summary>
    ///     The pause conflict's own arm, and the member that makes it worth having a member: <c>standingDecision</c>
    ///     tells the second person to click WHAT was decided rather than only that their click failed.
    /// </summary>
    [Test]
    public async Task TryHandleAsync_ForAGraphWorkflowGateAlreadyDecided_WritesTheStandingDecision()
    {
        var exception = new GraphWorkflowGateAlreadyDecidedException("Node run 'review' is Succeeded, so there is nothing to decide on it. It was answered Reject.",
            GraphWorkflowDecisionKind.Reject);

        var body = await HandleAsync(exception).ConfigureAwait(false);

        AssertEx.Equal("GraphWorkflowGateAlreadyDecided", body.ConflictType);
        AssertEx.Equal(expected: 409, body.Status);
        AssertEx.Equal(exception.Message, body.Detail);
        AssertEx.Equal("Reject", body.StandingDecision);
        AssertEx.Null(body.MaxConcurrentRuns, "a gate conflict carries no cap numbers.");
    }

    /// <summary>
    ///     An exception the switch does not name must be left alone, or every unmapped failure in the node would answer
    ///     409 instead of the 500 that says something is actually broken.
    /// </summary>
    [Test]
    public async Task TryHandleAsync_ForAnExceptionTheSwitchDoesNotName_DeclinesToHandleIt()
    {
        var context = new DefaultHttpContext
        {
            Response =
            {
                Body = new MemoryStream()
            }
        };
        var handler = new ConflictExceptionHandler(NullLogger<ConflictExceptionHandler>.Instance);

        AssertEx.False(await handler.TryHandleAsync(context, new InvalidOperationException("something else"), CancellationToken.None).ConfigureAwait(false));
    }

    /// <summary>The handler over a bare context, so the assertion is about the switch and the envelope rather than a route.</summary>
    private static async Task<ConflictProblemBody> HandleAsync(Exception exception)
    {
        var context = new DefaultHttpContext
        {
            TraceIdentifier = "trace-" + Guid.NewGuid().ToString("N"),
            Response =
            {
                Body = new MemoryStream()
            }
        };
        var handler = new ConflictExceptionHandler(NullLogger<ConflictExceptionHandler>.Instance);

        AssertEx.True(await handler.TryHandleAsync(context, exception, CancellationToken.None).ConfigureAwait(false));
        AssertEx.Equal(expected: 409, context.Response.StatusCode);
        AssertEx.Contains(context.Response.ContentType, "problem+json", StringComparison.OrdinalIgnoreCase);

        context.Response.Body.Position = 0;
        return AssertEx.NotNull(await JsonSerializer.DeserializeAsync<ConflictProblemBody>(context.Response.Body, JsonOptions).ConfigureAwait(false));
    }

    private static async Task<ConflictProblemBody> ExecuteAsync(TestServerWebAppFactory factory, string startText)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, ExecuteRoute)
        {
            Content = JsonContent.Create(new
            {
                graph = new
                {
                    startText,
                    nodes = Array.Empty<object>(),
                    edges = Array.Empty<object>()
                }
            })
        };
        factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        AssertEx.Contains(response.Content.Headers.ContentType?.ToString(), "problem+json", StringComparison.OrdinalIgnoreCase);
        return AssertEx.NotNull(await response.Content.ReadFromJsonAsync<ConflictProblemBody>(JsonOptions).ConfigureAwait(false));
    }

    private static TestServerWebAppFactory CreateFactory()
    {
        var execution = Substitute.For<IPreviewWorkflowExecutionService>();
        execution.StartAsync(Arg.Any<PreviewWorkflowGraph>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                 .Returns(call => string.Equals(call.Arg<PreviewWorkflowGraph>().StartText, RunCapStartText, StringComparison.Ordinal)
                     ? Task.FromException<Guid>(new PreviewWorkflowCapReachedException(maxConcurrentRuns: 3))
                     : Task.FromException<Guid>(new PreviewWorkflowModelCapExceededException(distinctModelCount: 5, maxLoadedProcesses: 2)));

        return new TestServerWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<IPreviewWorkflowExecutionService>();
                services.AddScoped(_ => execution);
            }
        };
    }

    private sealed record ConflictProblemBody(
        string ConflictType,
        string Title,
        int Status,
        string Detail,
        string TraceId,
        int? MaxConcurrentRuns,
        int? DistinctModelCount,
        int? MaxLoadedProcesses,
        string? StandingDecision);
}

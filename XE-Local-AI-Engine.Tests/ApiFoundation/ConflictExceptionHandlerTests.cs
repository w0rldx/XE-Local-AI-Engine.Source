namespace XE_Local_AI_Engine.Tests.ApiFoundation;

using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.ExceptionHandling;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows.Chat;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Pins the contract of <c>ConflictExceptionHandler</c>: one 409 <c>application/problem+json</c> envelope,
///     discriminated by <c>conflictType</c>, carrying the exception message as <c>detail</c>, a <c>traceId</c>, and —
///     where the refusal has one — the typed payload member an operator needs to act on it.
///     <para>
///         The arms asserted here are the ones no endpoint can provoke without staging a race, plus both branches of
///         <c>SetStandingDecision</c> and one arm that fills no payload member at all. The rest are pinned where they are
///         provoked end to end: <c>ReadOnlyConversation</c> in <c>NodeChatReadOnlyEndpointTests</c>,
///         <c>WorkspaceRevocationBusy</c> in <c>WorkspaceEndpointTests</c>, the worker/image conflicts in
///         <c>ConnectionEndpointTests</c>, and <c>InstalledModelHasDependentAdapters</c> in
///         <c>LocalModelEndpointTests</c>.
///     </para>
/// </summary>
[Category(TestCategories.Unit)]
public sealed class ConflictExceptionHandlerTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    ///     The plain arm: a mapped exception that sets no payload member at all still gets the whole envelope. Without
    ///     this, a regression that only ever filled the envelope from <c>SetStandingDecision</c> would go unnoticed.
    /// </summary>
    [Test]
    public async Task TryHandleAsync_ForAWorkSessionInvalidTransition_WritesTheEnvelopeWithNoExtras()
    {
        var exception = new WorkSessionInvalidTransitionException("A work session can only be started while it is Draft or Paused; this one is Running.");

        var body = await HandleAsync(exception);

        AssertEx.Equal("WorkSessionInvalidTransition", body.ConflictType);
        AssertEx.Equal("Conflict", body.Title);
        AssertEx.Equal(expected: 409, body.Status);
        AssertEx.Equal(exception.Message, body.Detail);
        AssertEx.NotEmpty(body.TraceId);
        AssertEx.Null(body.StandingDecision, "a transition refusal carries no standing decision.");
    }

    /// <summary>
    ///     The development-workflow gate's arm, and the other half of <c>SetStandingDecision</c>: <c>standingDecision</c>
    ///     tells the second person to click WHAT was decided rather than only that their click failed.
    /// </summary>
    [Test]
    public async Task TryHandleAsync_ForADevWorkflowGateAlreadyDecided_WritesTheStandingDecision()
    {
        var exception = new DevWorkflowGateAlreadyDecidedException("Node run 'review' was already decided Approve.", DevWorkflowDecisionKind.Approve);

        var body = await HandleAsync(exception);

        AssertEx.Equal("DevWorkflowGateAlreadyDecided", body.ConflictType);
        AssertEx.Equal(expected: 409, body.Status);
        AssertEx.Equal(exception.Message, body.Detail);
        AssertEx.Equal("Approve", body.StandingDecision);
        AssertEx.NotEmpty(body.TraceId);
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

        var body = await HandleAsync(exception);

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

        var body = await HandleAsync(exception);

        AssertEx.Equal("GraphWorkflowGateAlreadyDecided", body.ConflictType);
        AssertEx.Equal(expected: 409, body.Status);
        AssertEx.Equal(exception.Message, body.Detail);
        AssertEx.Equal("Reject", body.StandingDecision);
    }

    /// <summary>The four chat-workflow refusals each reach the client under a discriminator of their own, which the chat page branches on.</summary>
    [Test]
    [Arguments("steer-limit")]
    [Arguments("busy")]
    [Arguments("rerun")]
    [Arguments("attachments")]
    public async Task TryHandleAsync_ForAChatWorkflowRefusal_WritesItsOwnConflictType(string refusal)
    {
        var (exception, expected) = refusal switch
        {
            "busy" => ((Exception)new GraphWorkflowRunBusyException("busy"), "GraphWorkflowRunBusy"),
            "rerun" => (new GraphWorkflowRerunConfirmationRequiredException("confirm"), "GraphWorkflowRerunConfirmationRequired"),
            "steer-limit" => (new GraphWorkflowSteerLimitReachedException("capped"), "GraphWorkflowSteerLimitReached"),
            _ => (new GraphWorkflowAttachmentsNotAcceptedException("no files"), "GraphWorkflowAttachmentsNotAccepted")
        };

        var body = await HandleAsync(exception);

        AssertEx.Equal(expected, body.ConflictType);
        AssertEx.Equal(expected: 409, body.Status);
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

        AssertEx.False(await handler.TryHandleAsync(context, new InvalidOperationException("something else"), CancellationToken.None));
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

        AssertEx.True(await handler.TryHandleAsync(context, exception, CancellationToken.None));
        AssertEx.Equal(expected: 409, context.Response.StatusCode);
        AssertEx.Contains(context.Response.ContentType, "problem+json", StringComparison.OrdinalIgnoreCase);

        context.Response.Body.Position = 0;
        return AssertEx.NotNull(await JsonSerializer.DeserializeAsync<ConflictProblemBody>(context.Response.Body, JsonOptions));
    }

    private sealed record ConflictProblemBody(
        string ConflictType,
        string Title,
        int Status,
        string Detail,
        string TraceId,
        string? StandingDecision);
}

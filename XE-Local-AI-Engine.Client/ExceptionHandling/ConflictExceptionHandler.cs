namespace XE_Local_AI_Engine.Client.ExceptionHandling;

using Microsoft.AspNetCore.Diagnostics;
using Microsoft.IdentityModel.JsonWebTokens;
using XE_Local_AI_Engine.Client.Common.Extensions;
using XE_Local_AI_Engine.Client.Common.ProblemDetailModels;
using XE_Local_AI_Engine.Client.Common.ProblemDetailModels.Enums;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Connection;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Client.Services.PreviewWorkflows;
using XE_Local_AI_Engine.Client.Services.Workspace;
using XE_Local_AI_Engine.Providers.Abstractions.Image;

/// <summary>
///     The ONE place a typed domain exception becomes a 409. Endpoints must not hand-build conflict bodies: every
///     mapped exception answers with the same <see cref="ConflictProblemDetails" /> envelope, and the SPA
///     discriminates on <c>conflictType</c> (see <c>NodeChatConflict.ts</c>). Conflict payload beyond the message is a
///     typed, null-omitted member of that same envelope (declared on endpoints via <c>ProducesConflictProblemDetails()</c>),
///     so the envelope itself stays one shape and the OpenAPI schema names every member.
/// </summary>
public class ConflictExceptionHandler(ILogger<ConflictExceptionHandler> logger) : IExceptionHandler
{
    /// <summary>Same string FastEndpoints' ResponseSerializer writes, so a 409 looks like every other problem body.</summary>
    private const string ProblemContentType = "application/problem+json; charset=utf-8";

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        var conflictType = exception switch
        {
            NodeChatReadOnlyConversationException => NodeConflictProblemType.ReadOnlyConversation,
            ImageModelInUseException => NodeConflictProblemType.ImageModelInUse,
            WorkerNotPairedException => NodeConflictProblemType.WorkerNotPaired,
            WorkerTokenExpiredException => NodeConflictProblemType.WorkerTokenExpired,
            WorkspaceRevocationBusyException => NodeConflictProblemType.WorkspaceRevocationBusy,
            PreviewWorkflowCapReachedException => NodeConflictProblemType.PreviewWorkflowCapReached,
            PreviewWorkflowModelCapExceededException => NodeConflictProblemType.PreviewWorkflowModelCapExceeded,
            InstalledModelDependentAdaptersException => NodeConflictProblemType.InstalledModelHasDependentAdapters,
            InstalledModelProviderConflictException => NodeConflictProblemType.InstalledModelProviderConflict,
            InstalledModelProviderMapSupersededException => NodeConflictProblemType.InstalledModelProviderMapSuperseded,
            ModelOperationNotSupportedByProviderException => NodeConflictProblemType.ModelOperationNotSupportedByProvider,
            WorkSessionInvalidTransitionException => NodeConflictProblemType.WorkSessionInvalidTransition,
            WorkSessionConcurrencyException => NodeConflictProblemType.WorkSessionVersionConflict,
            DevWorkflowGateAlreadyDecidedException => NodeConflictProblemType.DevWorkflowGateAlreadyDecided,
            DevWorkflowRunInFlightException => NodeConflictProblemType.DevWorkflowRunInFlight,
            DevWorkflowInvalidTransitionException => NodeConflictProblemType.DevWorkflowInvalidTransition,
            DevWorkflowConcurrencyException => NodeConflictProblemType.DevWorkflowVersionConflict,
            GraphWorkflowDefinitionConflictException => NodeConflictProblemType.GraphWorkflowDefinitionConflict,
            GraphWorkflowRunConflictException => NodeConflictProblemType.GraphWorkflowRunConflict,

            // The store's own rejection channel, under the same member: a stale ExpectedVersion, a lost concurrency
            // race and a request id reused on another definition all reach a client as "re-read the run", and giving
            // them a member each would be three names for one instruction.
            GraphWorkflowInvalidTransitionException => NodeConflictProblemType.GraphWorkflowRunConflict,
            _ => (NodeConflictProblemType?)null
        };

        if (conflictType is null)
        {
            return false;
        }

        logger.LogWarning(exception,
            "Handled conflict exception while processing {Method} {Path}. StatusCode: {StatusCode}. TraceId: {TraceId}. UserId: {UserId}. ExceptionType: {ExceptionType}",
            RequestLogSanitizer.Sanitize(httpContext.Request.Method),
            RequestLogSanitizer.Sanitize(httpContext.Request.Path.Value),
            StatusCodes.Status409Conflict,
            httpContext.TraceIdentifier,
            httpContext.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value ?? "anonymous",
            exception.GetType().Name);

        httpContext.Response.StatusCode = StatusCodes.Status409Conflict;

        var problemDetails = new ConflictProblemDetails
        {
            Status = StatusCodes.Status409Conflict,
            Type = "https://tools.ietf.org/html/rfc7231#section-6.5.8",
            Title = "Conflict",
            ConflictType = conflictType.Value.ToString(),
            Detail = exception.Message ?? "Conflict"
        }.WithTraceId(httpContext);

        SetCapMembers(problemDetails, exception);

        // The content type MUST be passed here: WriteAsJsonAsync overwrites Response.ContentType with
        // application/json when it is not, which silently demoted this problem+json body.
        await httpContext.Response.WriteAsJsonAsync(problemDetails, options: null, ProblemContentType, cancellationToken).ConfigureAwait(false);

        return true;
    }

    /// <summary>
    ///     Carries the detail an operator needs to act on the refusal — the cap that was hit, or the decision that
    ///     already stands. They are typed members of the one conflict envelope (omitted when null) so the OpenAPI
    ///     schema names them; the wire body is the same as when they were problem-details extensions.
    /// </summary>
    private static void SetCapMembers(ConflictProblemDetails problemDetails, Exception exception)
    {
        switch (exception)
        {
            case PreviewWorkflowCapReachedException capReached:
                problemDetails.MaxConcurrentRuns = capReached.MaxConcurrentRuns;
                break;
            case PreviewWorkflowModelCapExceededException modelCap:
                problemDetails.DistinctModelCount = modelCap.DistinctModelCount;
                problemDetails.MaxLoadedProcesses = modelCap.MaxLoadedProcesses;
                break;
            case DevWorkflowGateAlreadyDecidedException alreadyDecided:
                problemDetails.StandingDecision = alreadyDecided.StandingDecision.ToString();
                break;
            default:
                break;
        }
    }
}

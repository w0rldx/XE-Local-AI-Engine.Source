namespace XE_Local_AI_Engine.Client.ExceptionHandling;

using Microsoft.AspNetCore.Diagnostics;
using Microsoft.IdentityModel.JsonWebTokens;
using XE_Local_AI_Engine.Client.Common.Extensions;
using XE_Local_AI_Engine.Client.Common.ProblemDetailModels;
using XE_Local_AI_Engine.Client.Common.ProblemDetailModels.Enums;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Connection;
using XE_Local_AI_Engine.Client.Services.PreviewWorkflows;
using XE_Local_AI_Engine.Client.Services.Workspace;
using XE_Local_AI_Engine.Providers.Abstractions.Image;

/// <summary>
///     The ONE place a typed domain exception becomes a 409. Endpoints must not hand-build conflict bodies: every
///     mapped exception answers with the same <see cref="ConflictProblemDetails" /> envelope, and the SPA
///     discriminates on <c>conflictType</c> (see <c>NodeChatConflict.ts</c>). Conflict payload beyond the message
///     travels as problem-details extensions alongside <c>traceId</c>, so the envelope itself stays one shape.
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
            _ => (NodeConflictProblemType?)null
        };

        if (conflictType is null)
        {
            return false;
        }

        logger.LogWarning(exception,
            "Handled conflict exception while processing {Method} {Path}. StatusCode: {StatusCode}. TraceId: {TraceId}. UserId: {UserId}. ExceptionType: {ExceptionType}",
            httpContext.Request.Method,
            httpContext.Request.Path,
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

        AddConflictExtensions(problemDetails, exception);

        // The content type MUST be passed here: WriteAsJsonAsync overwrites Response.ContentType with
        // application/json when it is not, which silently demoted this problem+json body.
        await httpContext.Response.WriteAsJsonAsync(problemDetails, options: null, ProblemContentType, cancellationToken).ConfigureAwait(false);

        return true;
    }

    /// <summary>
    ///     Carries the numbers an operator needs to act on a cap rejection. They ride as problem-details extensions
    ///     (like <c>traceId</c>) rather than as typed properties, so one conflict envelope serves every mapping.
    /// </summary>
    private static void AddConflictExtensions(ConflictProblemDetails problemDetails, Exception exception)
    {
        switch (exception)
        {
            case PreviewWorkflowCapReachedException capReached:
                problemDetails.Extensions["maxConcurrentRuns"] = capReached.MaxConcurrentRuns;
                break;
            case PreviewWorkflowModelCapExceededException modelCap:
                problemDetails.Extensions["distinctModelCount"] = modelCap.DistinctModelCount;
                problemDetails.Extensions["maxLoadedProcesses"] = modelCap.MaxLoadedProcesses;
                break;
            default:
                break;
        }
    }
}

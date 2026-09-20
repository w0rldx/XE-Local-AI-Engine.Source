namespace XE_Local_AI_Engine.Client.ExceptionHandling;

using Microsoft.AspNetCore.Diagnostics;
using Microsoft.IdentityModel.JsonWebTokens;
using XE_Local_AI_Engine.Client.Common.Extensions;
using XE_Local_AI_Engine.Client.Common.ProblemDetailModels;
using XE_Local_AI_Engine.Client.Common.ProblemDetailModels.Enums;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.ExternalApps;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Client.Services.Workspace;
using XE_Local_AI_Engine.Providers.Abstractions.Image;

/// <summary>
///     The ONE place a typed domain exception becomes a 409.
/// </summary>
/// <remarks>
///     Endpoints must not hand-build conflict bodies: every mapped exception answers with the same
///     <see cref="ConflictProblemDetails" /> envelope, declared via <c>ProducesConflictProblemDetails()</c>, and the SPA
///     discriminates on <c>conflictType</c> (see <c>NodeChatConflict.ts</c>). That rule governs domain conflicts: an
///     <b>operational block</b>, which the service reports as a returned outcome rather than by throwing, keeps its own
///     typed <c>*BlockedResponse</c> body and never reaches this handler — see ADR 0009.
/// </remarks>
public class ConflictExceptionHandler : IExceptionHandler
{
    /// <summary>Same string FastEndpoints' ResponseSerializer writes, so a 409 looks like every other problem body.</summary>
    private const string ProblemContentType = "application/problem+json; charset=utf-8";
    private readonly ILogger<ConflictExceptionHandler> _logger;

    public ConflictExceptionHandler(ILogger<ConflictExceptionHandler> logger)
    {
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        var conflictType = exception switch
        {
            NodeChatReadOnlyConversationException => NodeConflictProblemType.ReadOnlyConversation,
            ImageModelInUseException => NodeConflictProblemType.ImageModelInUse,
            WorkspaceRevocationBusyException => NodeConflictProblemType.WorkspaceRevocationBusy,
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
            GraphWorkflowGateAlreadyDecidedException => NodeConflictProblemType.GraphWorkflowGateAlreadyDecided,
            ExternalAppOperationInFlightException => NodeConflictProblemType.ExternalAppOperationInFlight,
            ExternalAppInvalidTransitionException => NodeConflictProblemType.ExternalAppInvalidTransition,
            ExternalAppAlreadyInstalledException => NodeConflictProblemType.ExternalAppAlreadyInstalled,
            ExternalAppPermissionChangeRequiresAcknowledgementException => NodeConflictProblemType.ExternalAppPermissionChangeRequiresAcknowledgement,
            ExternalAppManifestChangedException => NodeConflictProblemType.ExternalAppManifestChanged,
            ExternalAppConcurrencyException => NodeConflictProblemType.ExternalAppVersionConflict,

            // The store's own rejection channel, under the same member: a stale ExpectedVersion, a lost concurrency race and
            // a request id reused on another definition all reach a client as "re-read the run", one instruction, one name.
            GraphWorkflowInvalidTransitionException => NodeConflictProblemType.GraphWorkflowRunConflict,
            _ => (NodeConflictProblemType?)null
        };

        if (conflictType is null)
        {
            return false;
        }

        _logger.LogWarning(exception,
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
            // Do NOT "fix" this host to FastEndpoints' www.rfc-editor.org/rfc/ base: it is ALSO what ASP.NET Core's ProblemDetailsDefaults emits, so aligning moves this AWAY from the framework's.
            // Divergence pinned by DevelopmentExceptionHandlerTests; see docs/wiki/09-api-and-hubs.md ("Status choices worth their reasons").
            Type = "https://tools.ietf.org/html/rfc7231#section-6.5.8",
            Title = "Conflict",
            ConflictType = conflictType.Value.ToString(),
            Detail = exception.Message ?? "Conflict"
        }.WithTraceId(httpContext);

        SetTypedMembers(problemDetails, exception);

        // The content type MUST be passed here: WriteAsJsonAsync overwrites Response.ContentType with
        // application/json when it is not, which silently demoted this problem+json body.
        await httpContext.Response.WriteAsJsonAsync(problemDetails, options: null, ProblemContentType, cancellationToken);

        return true;
    }

    /// <summary>
    ///     Carries the detail an operator needs to act on the refusal — the decision that already stands, or the
    ///     permissions the refused update would add.
    /// </summary>
    /// <remarks>
    ///     Each is a typed member of the one conflict envelope, omitted when null, so the OpenAPI schema names it; the
    ///     wire body is the same as when they rode as problem-details extensions.
    /// </remarks>
    private static void SetTypedMembers(ConflictProblemDetails problemDetails, Exception exception)
    {
        switch (exception)
        {
            case DevWorkflowGateAlreadyDecidedException alreadyDecided:
                problemDetails.StandingDecision = alreadyDecided.StandingDecision.ToString();
                break;
            case GraphWorkflowGateAlreadyDecidedException graphGateDecided:
                problemDetails.StandingDecision = graphGateDecided.StandingDecision.ToString();
                break;
            case ExternalAppPermissionChangeRequiresAcknowledgementException permissionChange:
                problemDetails.AddedPermissions = permissionChange.AddedPermissions;
                break;
            default:
                break;
        }
    }
}

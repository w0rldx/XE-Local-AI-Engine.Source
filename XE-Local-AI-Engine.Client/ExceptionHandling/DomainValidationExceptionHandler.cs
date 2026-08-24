namespace XE_Local_AI_Engine.Client.ExceptionHandling;

using FluentValidation.Results;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.IdentityModel.JsonWebTokens;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Automation;
using XE_Local_AI_Engine.Client.Services.CustomTools;
using XE_Local_AI_Engine.Client.Services.Mcp;
using XE_Local_AI_Engine.Client.Services.Scheduler;
using XE_Local_AI_Engine.Client.Services.WorkSessions;
using ProblemDetails = FastEndpoints.ProblemDetails;

/// <summary>
///     Maps the single-message domain validation exceptions to a 400 whose body is byte-identical to the
///     <c>AddError(exception.Message) + Send.ErrorsAsync()</c> pair the endpoints used to write by hand: the same
///     FastEndpoints <see cref="ProblemDetails" /> DTO, built the way <c>ErrorOptions.ResponseBuilder</c> builds it
///     (instance = request path, traceId = <see cref="HttpContext.TraceIdentifier" />), serialized with the same
///     options and content type. New single-message validation exceptions belong in the switch below rather than in a
///     per-endpoint catch. Multi-error types (<c>PreviewWorkflowValidationException</c>) and aggregate ones
///     (<c>SelectedFolderValidationException</c>) are deliberately out of scope — they do not map to one failure.
/// </summary>
public sealed class DomainValidationExceptionHandler(ILogger<DomainValidationExceptionHandler> logger) : IExceptionHandler
{
    // FastEndpoints' ErrorOptions.GeneralErrorsField default — the property name AddError(message) attaches a
    // message-only failure to. Its getter is internal, so the literal is mirrored here; FE's ProblemDetails.Error
    // constructor runs it through the serializer's naming policy, which yields "generalErrors" in the response body.
    private const string GeneralErrorsField = "GeneralErrors";

    // ErrorOptions.ContentType + SerializerOptions.CharacterEncoding, the pair FE's ResponseSerializer concatenates.
    private const string ProblemContentType = "application/problem+json; charset=utf-8";

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is not (ScheduledJobValidationException
            or CustomToolValidationException
            or McpServerValidationException
            or SlashCommandValidationException
            or PlaybookActionValidationException
            or AgentDefinitionValidationException
            or AgentSkillValidationException
            or WorkSessionValidationException))
        {
            return false;
        }

        logger.LogWarning(exception,
            "Handled domain validation exception while processing {Method} {Path}. StatusCode: {StatusCode}. TraceId: {TraceId}. UserId: {UserId}. ExceptionType: {ExceptionType}",
            RequestLogSanitizer.Sanitize(httpContext.Request.Method),
            RequestLogSanitizer.Sanitize(httpContext.Request.Path.Value),
            StatusCodes.Status400BadRequest,
            httpContext.TraceIdentifier,
            httpContext.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value ?? "anonymous",
            exception.GetType().Name);

        httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;

        var problemDetails = new ProblemDetails([new ValidationFailure(GeneralErrorsField, exception.Message)],
            httpContext.Request.Path,
            httpContext.TraceIdentifier,
            StatusCodes.Status400BadRequest);

        // Serialized as object (like FE's ResponseSerializer) against the DI json options, which ConfigureServices
        // configures with the very same ConfigureJsonSerializerOptions that seeds FastEndpoints' Config.Serializer.
        await httpContext.Response.WriteAsJsonAsync<object>(problemDetails, options: null, ProblemContentType, cancellationToken).ConfigureAwait(false);

        return true;
    }
}

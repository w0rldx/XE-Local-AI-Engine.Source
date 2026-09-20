namespace XE_Local_AI_Engine.Client.ExceptionHandling;

using Microsoft.AspNetCore.Diagnostics;
using Microsoft.IdentityModel.JsonWebTokens;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     The Development family's 409s, in one place instead of once per endpoint.
/// </summary>
/// <remarks>
///     Writes <c>AddError(exception.Message) + Send.ErrorsAsync(statusCode: 409)</c>'s body through
///     <see cref="FastEndpointsProblemWriter" /> — a relocation, not a reshape — and deliberately does NOT route
///     through <see cref="ConflictExceptionHandler" />, whose envelope is a different body.
///     <c>DevelopmentWorkspaceSecurityException</c> and its derived type are absent on purpose: no single status is
///     correct for them. See docs/wiki/09-api-and-hubs.md ("Status choices worth their reasons").
/// </remarks>
public sealed class DevelopmentConflictExceptionHandler : IExceptionHandler
{
    private readonly ILogger<DevelopmentConflictExceptionHandler> _logger;

    public DevelopmentConflictExceptionHandler(ILogger<DevelopmentConflictExceptionHandler> logger)
    {
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is not (DevelopmentInvalidTransitionException or DevelopmentConcurrencyException))
        {
            return false;
        }

        _logger.LogWarning(exception,
            "Handled Development conflict exception while processing {Method} {Path}. StatusCode: {StatusCode}. TraceId: {TraceId}. UserId: {UserId}. ExceptionType: {ExceptionType}",
            RequestLogSanitizer.Sanitize(httpContext.Request.Method),
            RequestLogSanitizer.Sanitize(httpContext.Request.Path.Value),
            StatusCodes.Status409Conflict,
            httpContext.TraceIdentifier,
            httpContext.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value ?? "anonymous",
            exception.GetType().Name);

        await FastEndpointsProblemWriter.WriteAsync(httpContext, exception.Message, StatusCodes.Status409Conflict, cancellationToken);

        return true;
    }
}

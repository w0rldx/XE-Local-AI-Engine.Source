namespace XE_Local_AI_Engine.Client.ExceptionHandling;

using Microsoft.AspNetCore.Diagnostics;
using Microsoft.IdentityModel.JsonWebTokens;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     The Development family's 409s, in one place instead of once per endpoint. Every endpoint that raised these
///     answered them with <c>AddError(exception.Message) + Send.ErrorsAsync(statusCode: 409)</c>, and this writes that
///     same body at that same status through <see cref="FastEndpointsProblemWriter" /> — a relocation, not a reshape.
///     <para>
///         It deliberately does NOT route through <see cref="ConflictExceptionHandler" />, whose
///         <c>ConflictProblemDetails</c> envelope is a different body (the message moves from the errors array to
///         <c>detail</c>, <c>title</c> becomes "Conflict", and a <c>conflictType</c> discriminator appears). Folding
///         Development into that envelope is worth doing, but it is an OpenAPI-visible contract change that needs its
///         own <c>NodeConflictProblemType</c> members and a regenerated client — not a side effect of removing catches.
///     </para>
///     <para>
///         <c>DevelopmentWorkspaceSecurityException</c> is absent on purpose: the patch and next-action endpoints
///         answer it 409 while the register/create/reconnect ones answer it 400, so no single status is correct for it
///         here. Its derived <c>DevelopmentRepositoryStateConflictException</c> is absent for the same reason — the
///         reconnect endpoint still catches the base type locally, and would swallow the derived one before it ever
///         reached a global handler.
///     </para>
/// </summary>
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

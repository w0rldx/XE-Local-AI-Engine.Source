namespace XE_Local_AI_Engine.Client.ExceptionHandling;

using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.JsonWebTokens;
using XE_Local_AI_Engine.Client.Common.Extensions;

/// <summary>
///     Represents default exception handler.
/// </summary>
public class DefaultExceptionHandler(ILogger<DefaultExceptionHandler> logger, IHostEnvironment hostEnvironment) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        var isDevelopment = hostEnvironment.IsDevelopment()
                            || hostEnvironment.IsEnvironment("Testing")
                            || hostEnvironment.IsEnvironment("IntegrationTests");
        var detail = isDevelopment ? exception.Message : "An unexpected error occurred";

        // Log the same W3C trace id the client receives in the ProblemDetails response (via ResolveTraceId), plus the
        // current span id, so a client-reported trace id joins straight to this log line and to distributed traces. The
        // Kestrel connection id is kept separately as RequestId — it identifies the connection, not the W3C trace.
        logger.LogError(exception,
            "Unhandled exception while processing {Method} {Path}. StatusCode: {StatusCode}. TraceId: {TraceId}. SpanId: {SpanId}. RequestId: {RequestId}. UserId: {UserId}. ExceptionType: {ExceptionType}",
            httpContext.Request.Method,
            httpContext.Request.Path,
            StatusCodes.Status500InternalServerError,
            ProblemDetailsExtensions.ResolveTraceId(httpContext),
            Activity.Current?.SpanId.ToString(),
            httpContext.TraceIdentifier,
            httpContext.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value ?? "anonymous",
            exception.GetType().Name);

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;

        var problemDetails = new ProblemDetails
        {
            Status = StatusCodes.Status500InternalServerError,
            Type = "https://tools.ietf.org/html/rfc7231#section-6.6.1",
            Title = "An unexpected error occurred",
            Detail = detail
        }.WithTraceId(httpContext);

        // The content type MUST be passed here: WriteAsJsonAsync overwrites Response.ContentType with application/json,
        // so setting the property beforehand is dead (the same trap ConflictExceptionHandler had).
        await httpContext.Response.WriteAsJsonAsync(problemDetails, options: null, "application/problem+json; charset=utf-8", cancellationToken).ConfigureAwait(false);

        return true;
    }
}

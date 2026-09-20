namespace XE_Local_AI_Engine.Client.ExceptionHandling;

using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.JsonWebTokens;
using XE_Local_AI_Engine.Client.Common.Extensions;

/// <summary>
///     Represents default exception handler.
/// </summary>
public class DefaultExceptionHandler : IExceptionHandler
{
    private readonly ILogger<DefaultExceptionHandler> _logger;
    private readonly IHostEnvironment _hostEnvironment;

    public DefaultExceptionHandler(ILogger<DefaultExceptionHandler> logger, IHostEnvironment hostEnvironment)
    {
        _logger = logger;
        _hostEnvironment = hostEnvironment;
    }

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        var isDevelopment = _hostEnvironment.IsDevelopment()
                            || _hostEnvironment.IsEnvironment("Testing")
                            || _hostEnvironment.IsEnvironment("IntegrationTests");
        var detail = isDevelopment ? exception.Message : "An unexpected error occurred";

        // Log the same W3C trace id the client receives in the ProblemDetails response (via ResolveTraceId), plus the span
        // id, so a reported id joins this line and distributed traces. RequestId stays the connection id, not the W3C trace.
        _logger.LogError(exception,
            "Unhandled exception while processing {Method} {Path}. StatusCode: {StatusCode}. TraceId: {TraceId}. SpanId: {SpanId}. RequestId: {RequestId}. UserId: {UserId}. ExceptionType: {ExceptionType}",
            RequestLogSanitizer.Sanitize(httpContext.Request.Method),
            RequestLogSanitizer.Sanitize(httpContext.Request.Path.Value),
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
        await httpContext.Response.WriteAsJsonAsync(problemDetails, options: null, "application/problem+json; charset=utf-8", cancellationToken);

        return true;
    }
}

namespace XE_Local_AI_Engine.Client.ExceptionHandling;

using Microsoft.AspNetCore.Diagnostics;

/// <summary>
///     Answers the 413 a capped route DECLARES when the host is the one that refuses the body.
/// </summary>
/// <remarks>
///     Kestrel enforces the cap as it READS, inside model binding and before any handler code runs, and its refusal is
///     a <see cref="BadHttpRequestException" /> carrying status 413. Transport-level rather than per-family, so every
///     route that declares a body cap reaches the same throw and the same answer. See
///     docs/wiki/09-api-and-hubs.md ("Status choices worth their reasons").
/// </remarks>
public sealed class RequestBodyTooLargeExceptionHandler : IExceptionHandler
{
    private readonly ILogger<RequestBodyTooLargeExceptionHandler> _logger;

    public RequestBodyTooLargeExceptionHandler(ILogger<RequestBodyTooLargeExceptionHandler> logger)
    {
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        // The STATUS is the discriminator, not the type: BadHttpRequestException is also how the host reports a malformed
        // request line, a bad chunk and a too-long header, each a 400 this must leave to the handler that owns it.
        if (exception is not BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge })
        {
            return false;
        }

        _logger.LogWarning(exception,
            "Refused an oversized request body while processing {Method} {Path}. StatusCode: {StatusCode}. TraceId: {TraceId}.",
            RequestLogSanitizer.Sanitize(httpContext.Request.Method),
            RequestLogSanitizer.Sanitize(httpContext.Request.Path.Value),
            StatusCodes.Status413PayloadTooLarge,
            httpContext.TraceIdentifier);

        // The same writer the endpoints' own Content-Length exit uses; only the detail differs between the two.
        await RequestBodyTooLargeProblem.WriteAsync(httpContext, "The request body is larger than this route accepts.", cancellationToken);

        return true;
    }
}

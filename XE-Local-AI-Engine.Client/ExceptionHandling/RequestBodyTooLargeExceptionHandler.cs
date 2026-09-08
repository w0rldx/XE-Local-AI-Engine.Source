namespace XE_Local_AI_Engine.Client.ExceptionHandling;

using Microsoft.AspNetCore.Diagnostics;

/// <summary>
///     Answers the 413 a capped route DECLARES when the host is the one that refuses the body.
///     <para>
///         A route carrying <c>IRequestSizeLimitMetadata</c> has two refusal paths and only one of them was answered.
///         The endpoint's own early exit reads Content-Length and sends the declared 413 — but Kestrel enforces the
///         cap as it READS, and it does that inside model binding, before any handler code runs. Its refusal is a
///         <see cref="BadHttpRequestException" /> carrying status 413, nothing in the pipeline mapped it, and
///         <c>DefaultExceptionHandler</c> turned the one refusal a real connection actually takes into a 500.
///     </para>
///     <para>
///         Transport-level rather than per-family, so it is one handler rather than one per capped route: every route
///         that declares a body cap — the graph-workflow four, the development-workflow two, and any added later —
///         reaches the same throw and now the same answer.
///     </para>
/// </summary>
public sealed class RequestBodyTooLargeExceptionHandler(ILogger<RequestBodyTooLargeExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        // The STATUS is the discriminator, not the type: BadHttpRequestException is also how the host reports a
        // malformed request line, a bad chunk and a too-long header, and every one of those is a 400 this must leave
        // to the handler that owns it.
        if (exception is not BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge })
        {
            return false;
        }

        logger.LogWarning(exception,
            "Refused an oversized request body while processing {Method} {Path}. StatusCode: {StatusCode}. TraceId: {TraceId}.",
            RequestLogSanitizer.Sanitize(httpContext.Request.Method),
            RequestLogSanitizer.Sanitize(httpContext.Request.Path.Value),
            StatusCodes.Status413PayloadTooLarge,
            httpContext.TraceIdentifier);

        httpContext.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;

        // Shared with the endpoints' own Content-Length exit so the two emitters of this status cannot drift apart.
        var problemDetails = RequestBodyTooLargeProblem.Create(httpContext, "The request body is larger than this route accepts.");

        // The content type MUST be passed here: WriteAsJsonAsync overwrites Response.ContentType with
        // application/json when it is not, which silently demotes the problem body (the trap every sibling carries).
        await httpContext.Response.WriteAsJsonAsync(problemDetails, options: null, RequestBodyTooLargeProblem.ContentType, cancellationToken).ConfigureAwait(false);

        return true;
    }
}

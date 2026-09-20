namespace XE_Local_AI_Engine.Client.ExceptionHandling;

using Microsoft.AspNetCore.Mvc;
using XE_Local_AI_Engine.Client.Common.Extensions;

/// <summary>
///     The ONE 413 answer a capped route gives — status, headers and body — written here so the two emitters cannot
///     drift apart.
/// </summary>
/// <remarks>
///     Both refusal paths go through <see cref="WriteAsync" />: the handler awaits it, and an endpoint sends
///     <see cref="Result" /> — whose only job is to call it — through <c>Send.ResultAsync</c>. Only
///     <see cref="ProblemDetails.Detail" /> differs, because the host knows nothing but "too large" while an endpoint
///     can name the cap it enforces. See docs/wiki/09-api-and-hubs.md ("Status choices worth their reasons").
/// </remarks>
public static class RequestBodyTooLargeProblem
{
    /// <summary>Same string every other problem body on this surface is written with.</summary>
    public const string ContentType = "application/problem+json; charset=utf-8";

    /// <summary>
    ///     Writes the whole answer: status, <see cref="ContentType" /> and body. The single writer, so a change here
    ///     reaches the host's refusal and the endpoints' own in one edit.
    /// </summary>
    /// <param name="httpContext">The request being refused; supplies the <c>traceId</c> every problem body carries.</param>
    /// <param name="detail">Operator-safe explanation, written as the ProblemDetails <c>detail</c>.</param>
    /// <param name="cancellationToken">Cancellation for the body write.</param>
    public static Task WriteAsync(HttpContext httpContext, string detail, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        httpContext.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;

        var problemDetails = new ProblemDetails
        {
            Status = StatusCodes.Status413PayloadTooLarge,
            Type = "https://tools.ietf.org/html/rfc7231#section-6.5.11",
            Title = "Request body too large",
            Detail = detail
        }.WithTraceId(httpContext);

        // The content type MUST be passed here: WriteAsJsonAsync overwrites Response.ContentType with
        // application/json when it is not, which silently demotes the problem body (the trap every sibling carries).
        return httpContext.Response.WriteAsJsonAsync(problemDetails, options: null, ContentType, cancellationToken);
    }

    /// <summary>
    ///     The same answer as an <see cref="IResult" />, for an endpoint that refuses the request itself before the
    ///     host ever reads the body. Sent with <c>Send.ResultAsync(...)</c>.
    /// </summary>
    public static IResult Result(string detail) =>
        new RequestBodyTooLargeResult(detail);

    /// <summary>
    ///     Deliberately not <c>Results.Problem</c>: that writes the bare <c>application/problem+json</c> media type
    ///     and serializes the body itself, which is a second emitter of this answer and the drift this type exists to
    ///     stop.
    /// </summary>
    private sealed class RequestBodyTooLargeResult : IResult
    {
        private readonly string _detail;

        public RequestBodyTooLargeResult(string detail)
        {
            _detail = detail;
        }

        public Task ExecuteAsync(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);

            return WriteAsync(httpContext, _detail, httpContext.RequestAborted);
        }
    }
}

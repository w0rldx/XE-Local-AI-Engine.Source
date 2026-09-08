namespace XE_Local_AI_Engine.Client.ExceptionHandling;

using Microsoft.AspNetCore.Mvc;
using XE_Local_AI_Engine.Client.Common.Extensions;

/// <summary>
///     The ONE 413 body a capped route answers with, built here so the two emitters cannot drift apart.
///     <para>
///         A capped route has two refusal paths and they used to write two different shapes for the same status:
///         <see cref="RequestBodyTooLargeExceptionHandler" /> wrote ASP.NET <see cref="ProblemDetails" /> when Kestrel
///         refused the body mid-read, while the endpoint's own Content-Length exit sent FastEndpoints' <c>errors[]</c>
///         body. Both routes DECLARE <c>ProducesProblem(413)</c> — the ProblemDetails schema — so the second one was a
///         contract lie the generated client encodes. Both now build from here.
///     </para>
///     <para>
///         Only <see cref="ProblemDetails.Detail" /> differs between them: the host knows nothing but "too large",
///         while an endpoint can name the cap it enforces.
///     </para>
/// </summary>
public static class RequestBodyTooLargeProblem
{
    /// <summary>Same string every other problem body on this surface is written with.</summary>
    public const string ContentType = "application/problem+json; charset=utf-8";

    public const string ProblemTitle = "Request body too large";

    public const string ProblemType = "https://tools.ietf.org/html/rfc7231#section-6.5.11";

    /// <param name="httpContext">The request being refused; supplies the <c>traceId</c> every problem body carries.</param>
    /// <param name="detail">Operator-safe explanation, written as the ProblemDetails <c>detail</c>.</param>
    public static ProblemDetails Create(HttpContext httpContext, string detail)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        return new ProblemDetails
        {
            Status = StatusCodes.Status413PayloadTooLarge,
            Type = ProblemType,
            Title = ProblemTitle,
            Detail = detail
        }.WithTraceId(httpContext);
    }

    /// <summary>
    ///     The same body as an <see cref="IResult" />, for an endpoint that refuses the request itself. Sent with
    ///     <c>Send.ResultAsync(...)</c>, which writes it as <c>application/problem+json</c>.
    /// </summary>
    public static IResult Result(HttpContext httpContext, string detail) => Results.Problem(Create(httpContext, detail));
}

namespace XE_Local_AI_Engine.Client.Common.Extensions;

using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;

/// <summary>
///     Represents problem details extensions.
/// </summary>
public static class ProblemDetailsExtensions
{
    public static TProblemDetails WithTraceId<TProblemDetails>(this TProblemDetails problemDetails, HttpContext httpContext)
        where TProblemDetails : ProblemDetails
    {
        ArgumentNullException.ThrowIfNull(problemDetails);
        ArgumentNullException.ThrowIfNull(httpContext);

        problemDetails.Extensions["traceId"] = ResolveTraceId(httpContext);

        return problemDetails;
    }

    /// <summary>
    ///     Resolves the trace id surfaced to the client and logged for a request. Prefers the W3C trace id from the
    ///     current <see cref="Activity" /> (created from the inbound <c>traceparent</c>, see <c>Program.cs</c>) so the id
    ///     correlates with the frontend diagnostics snapshot, backend logs, and distributed traces. Falls back to the
    ///     Kestrel connection id (<see cref="HttpContext.TraceIdentifier" />) when no Activity is present (e.g. a request
    ///     that arrives without a trace context). Callers logging alongside a <see cref="ProblemDetails" /> response must
    ///     use this so the logged trace id equals the one the client received.
    /// </summary>
    public static string ResolveTraceId(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        return Activity.Current?.TraceId.ToString() ?? httpContext.TraceIdentifier;
    }
}

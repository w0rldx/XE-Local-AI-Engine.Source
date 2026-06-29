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

        // Prefer the W3C trace id from the current Activity (created from the inbound `traceparent`, see Program.cs)
        // so the id correlates with the frontend diagnostics snapshot and backend logs. Falls back to the Kestrel
        // connection id when no Activity is present (e.g. a request that arrives without a trace context).
        problemDetails.Extensions["traceId"] = Activity.Current?.TraceId.ToString() ?? httpContext.TraceIdentifier;

        return problemDetails;
    }
}

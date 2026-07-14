namespace XE_Local_AI_Engine.Client.Hosting;

using System.Diagnostics;

/// <summary>
///     Builds the W3C <c>traceresponse</c> header value emitted on the success path so a local diagnostics snapshot can
///     correlate a 2xx response with backend logs (the error path carries the same id via <c>ProblemDetails.traceId</c>).
/// </summary>
internal static class TraceResponseHeader
{
    /// <summary>The response header name.</summary>
    internal const string HeaderName = "traceresponse";

    /// <summary>
    ///     Formats the <c>traceresponse</c> value for <paramref name="activity" />. The trace-flags byte reflects the
    ///     activity's actual <see cref="ActivityTraceFlags.Recorded" /> state rather than a hardcoded <c>01</c>, so a
    ///     downstream reader is not told the span was sampled when it was not: a recorded activity yields the <c>-01</c>
    ///     suffix and a not-recorded one yields <c>-00</c>.
    /// </summary>
    internal static string Build(Activity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);

        var flags = (activity.ActivityTraceFlags & ActivityTraceFlags.Recorded) != ActivityTraceFlags.None ? "01" : "00";
        return $"00-{activity.TraceId}-{activity.SpanId}-{flags}";
    }
}

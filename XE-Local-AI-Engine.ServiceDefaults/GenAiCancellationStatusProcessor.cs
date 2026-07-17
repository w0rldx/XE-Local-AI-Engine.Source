namespace Microsoft.Extensions.Hosting;

using System.Diagnostics;
using System.Linq;
using OpenTelemetry;

/// <summary>
///     Downgrades a gen_ai span's status from <see cref="ActivityStatusCode.Error" /> to
///     <see cref="ActivityStatusCode.Unset" /> when the span failed only because the operation was cancelled — a user
///     pressing Stop, not a service fault (GPTAUD-19a). MEAI's <c>OpenTelemetryChatClient</c> (source
///     <c>Microsoft.Extensions.AI</c>) records an <see cref="OperationCanceledException" /> /
///     <c>TaskCanceledException</c> as an Error-status span with the full cancellation stack; left as-is, every user
///     Stop pollutes error dashboards and alerting. This runs on <see cref="OnEnd" /> (before export, where the
///     already-stopped Activity's status can still be mutated) and touches ONLY gen_ai spans that indicate cancellation
///     — a genuinely failed span (any other exception type) is left untouched. The <c>error.type</c> tag is deliberately
///     kept as the cancellation marker per the GenAI semantic conventions.
/// </summary>
public sealed class GenAiCancellationStatusProcessor : BaseProcessor<Activity>
{
    // The gen_ai spans are emitted from the MEAI OpenTelemetryChatClient under this exact source name (the sourceName
    // passed to UseOpenTelemetry); a prefix match also covers any versioned/suffixed variant of the same source.
    private const string GenAiSourcePrefix = "Microsoft.Extensions.AI";

    public override void OnEnd(Activity data)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (data.Status != ActivityStatusCode.Error)
        {
            return;
        }

        if (!data.Source.Name.StartsWith(GenAiSourcePrefix, StringComparison.Ordinal))
        {
            return;
        }

        if (!IndicatesCancellation(data))
        {
            return;
        }

        // Cancellation is a normal outcome, not a service error. Keep error.type (semconv cancellation marker) and the
        // recorded exception event intact; only the status is downgraded so the turn no longer reads as a failure.
        data.SetStatus(ActivityStatusCode.Unset);
    }

    private static bool IndicatesCancellation(Activity activity)
    {
        if (IsCancellationTypeName(activity.GetTagItem("error.type") as string))
        {
            return true;
        }

        // An "exception" activity event records the exception type under the exception.type tag; a cancellation there
        // is the streaming-path signal (SetStatus may carry no error.type). LINQ form keeps Sonar S3267 satisfied.
        return activity.Events
                       .Where(activityEvent => string.Equals(activityEvent.Name, "exception", StringComparison.Ordinal))
                       .SelectMany(activityEvent => activityEvent.Tags)
                       .Any(tag => string.Equals(tag.Key, "exception.type", StringComparison.Ordinal)
                                   && IsCancellationTypeName(tag.Value as string));
    }

    private static bool IsCancellationTypeName(string? typeName)
    {
        // TaskCanceledException derives from OperationCanceledException; match either fully-qualified or short form.
        return typeName is not null
               && (typeName.Contains("OperationCanceledException", StringComparison.Ordinal)
                   || typeName.Contains("TaskCanceledException", StringComparison.Ordinal));
    }
}

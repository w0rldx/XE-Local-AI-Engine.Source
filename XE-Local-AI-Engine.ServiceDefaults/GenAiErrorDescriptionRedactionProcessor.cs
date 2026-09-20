namespace Microsoft.Extensions.Hosting;

using System.Diagnostics;
using OpenTelemetry;

/// <summary>
///     Redacts the status description of a failed gen_ai span down to its <c>error.type</c> value, so a failure path
///     exports the same metadata-only shape the success path does.
/// </summary>
/// <remarks>
///     MEAI's telemetry hop records a failure with
///     <c>activity.AddTag("error.type", …).SetStatus(ActivityStatusCode.Error, error.Message)</c> — unconditionally, with no regard for
///     <c>EnableSensitiveData</c>. Provider exception messages are not metadata: a <c>ClientResultException</c> from llama-server or any
///     OpenAI-compatible endpoint embeds the raw HTTP response body, and other provider exceptions quote the request text, so every gen_ai
///     span would ship conversation content in <see cref="Activity.StatusDescription" /> the moment a call failed.
/// </remarks>
public sealed class GenAiErrorDescriptionRedactionProcessor : BaseProcessor<Activity>
{
    // Same source match as GenAiCancellationStatusProcessor: MEAI emits gen_ai spans under this exact source name and
    // a prefix match also covers any versioned/suffixed variant of it.
    private const string GenAiSourcePrefix = "Microsoft.Extensions.AI";

    /// <summary>
    ///     Rewrites a failed gen_ai span's description at the export boundary, leaving tags and events as recorded.
    /// </summary>
    /// <remarks>
    ///     The sites that must stay metadata-only on their failure paths include the three background ones —
    ///     conversation summarizer, memory extraction, playbook analysis — that exist to hold a node-boundary
    ///     invariant on conversation content, and the two embedding hops over memory and knowledge-base text.
    ///     <c>error.type</c> is the semantic-convention low-cardinality marker, an exception type name, which is what a
    ///     dashboard actually groups on; with no such tag there is nothing safe to say and the description is cleared.
    /// </remarks>
    public override void OnEnd(Activity data)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (data.Status != ActivityStatusCode.Error || string.IsNullOrEmpty(data.StatusDescription))
        {
            return;
        }

        if (!data.Source.Name.StartsWith(GenAiSourcePrefix, StringComparison.Ordinal))
        {
            return;
        }

        // Keeps the span an error and keeps every tag and event; only the free-text description is replaced.
        data.SetStatus(ActivityStatusCode.Error, data.GetTagItem("error.type") as string);
    }
}

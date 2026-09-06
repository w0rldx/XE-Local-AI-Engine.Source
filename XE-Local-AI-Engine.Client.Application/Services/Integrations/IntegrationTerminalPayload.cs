namespace XE_Local_AI_Engine.Client.Services.Integrations;

using System.Text.Json;
using System.Text.Json.Serialization;
using XE_Local_AI_Engine.Client.Services.Integrations.Implementation;

/// <summary>
///     The terminal events' payloads: <c>execution.completed</c> is <c>{tokens?, durationMs}</c> and
///     <c>execution.failed</c> is <c>{category, summary}</c>, per the brief's envelope.
///     <para>
///         Built ONCE per terminal and used twice — the bytes persisted as the terminal row's detail and the element
///         published on the stream event are the same JSON. A caller that misses the frame and replays from the poll
///         route must not be handed a different envelope from the one the stream would have given it.
///     </para>
/// </summary>
internal static class IntegrationTerminalPayload
{
    /// <summary>
    ///     The summary is the only unbounded part of either payload, and the store refuses an event detail over 4 KiB.
    ///     Cut here rather than at the store, so the persisted bytes and the published element stay identical.
    /// </summary>
    private const int MaxSummaryBytes = 3_500;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        // `tokens?` is optional in the contract: a run whose provider reported no usage omits the field rather than
        // publishing a null the caller has to special-case.
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static JsonElement Failure(string? category, string? summary) =>
        JsonSerializer.SerializeToElement(new
            {
                category,
                summary = summary is null ? null : IntegrationStreamEventMapper.TruncateToUtf8ByteBudget(summary, MaxSummaryBytes)
            },
            Options);

    public static JsonElement Completion(int? tokens, long durationMs) =>
        JsonSerializer.SerializeToElement(new
            {
                tokens,
                durationMs
            },
            Options);
}

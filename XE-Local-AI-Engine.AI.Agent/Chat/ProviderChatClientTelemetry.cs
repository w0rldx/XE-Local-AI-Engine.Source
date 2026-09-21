namespace XE_Local_AI_Engine.AI.Agent.Chat;

using Microsoft.Extensions.AI;

/// <summary>
///     Applies the gen_ai OpenTelemetry hop to a chat client a background job built for itself rather than resolved
///     from DI, so bypassing <c>DecorateChatClientPipeline</c> does not also cost it its span.
/// </summary>
/// <remarks>
///     Those clients — one from <c>ILocalModelProvider.CreateChatClient(...)</c> or a transient llama-server endpoint —
///     bypass the pipeline on purpose, because for them the node-boundary invariant is which provider serves them.
///     See docs/wiki/04-agent-mode.md ("Why each hop sits where it does, and what it may mutate").
/// </remarks>
public static class ProviderChatClientTelemetry
{
    /// <summary>
    ///     The activity-source name, pinned to match the one <c>DecorateChatClientPipeline</c> passes to
    ///     <c>UseOpenTelemetry</c>; keep the two literals in step.
    /// </summary>
    /// <remarks>
    ///     MEAI's own default (<c>Experimental.Microsoft.Extensions.AI</c>) does NOT match the ServiceDefaults wildcard
    ///     <c>AddSource("Microsoft.Extensions.AI*")</c>, so a span emitted under it is never exported.
    /// </remarks>
    public const string ActivitySourceName = "Microsoft.Extensions.AI";

    /// <summary>
    ///     Wraps <paramref name="chatClient" /> in a metadata-only gen_ai telemetry hop: model id, token counts,
    ///     latency, finish reason and tool names, never message content. Disposing the returned client disposes
    ///     <paramref name="chatClient" />.
    /// </summary>
    /// <remarks>
    ///     <c>EnableSensitiveData</c> is hard-coded false and deliberately does NOT read
    ///     <c>AgentTelemetryOptions.CaptureSensitiveContent</c>: the three callers hold a node-boundary invariant on
    ///     conversation content, which the operator's interactive-pipeline opt-in would export. Setting it explicitly
    ///     also beats the ambient <c>OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT</c>, which Aspire injects as
    ///     true.
    /// </remarks>
    public static IChatClient WithProviderTelemetry(this IChatClient chatClient)
    {
        ArgumentNullException.ThrowIfNull(chatClient);

        return chatClient.AsBuilder()
                         .UseOpenTelemetry(sourceName: ActivitySourceName,
                             configure: static openTelemetryChatClient => openTelemetryChatClient.EnableSensitiveData = false)
                         .Build();
    }
}

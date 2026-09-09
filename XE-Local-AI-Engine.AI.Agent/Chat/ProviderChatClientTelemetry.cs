namespace XE_Local_AI_Engine.AI.Agent.Chat;

using Microsoft.Extensions.AI;

/// <summary>
///     Applies the gen_ai OpenTelemetry hop to a chat client a background job built for itself — one resolved from
///     <c>ILocalModelProvider.CreateChatClient(...)</c> or from a transient llama-server endpoint — rather than
///     resolved from DI. Those clients bypass <c>AgentServiceCollectionExtensions.DecorateChatClientPipeline</c> on
///     purpose (the node-boundary invariant is which provider serves them), which also cost them their span.
/// </summary>
public static class ProviderChatClientTelemetry
{
    /// <summary>
    ///     The activity-source name, pinned to match the one <c>DecorateChatClientPipeline</c> passes to
    ///     <c>UseOpenTelemetry</c>. MEAI's own default (<c>Experimental.Microsoft.Extensions.AI</c>) does NOT match the
    ///     ServiceDefaults wildcard <c>AddSource("Microsoft.Extensions.AI*")</c>, so a span emitted under it is never
    ///     exported. Keep the two literals in step.
    /// </summary>
    public const string ActivitySourceName = "Microsoft.Extensions.AI";

    /// <summary>
    ///     Wraps <paramref name="chatClient" /> in a metadata-only gen_ai telemetry hop: model id, token counts,
    ///     latency, finish reason and tool names, never message content. Disposing the returned client disposes
    ///     <paramref name="chatClient" />.
    /// </summary>
    /// <remarks>
    ///     <c>EnableSensitiveData</c> is hard-coded false and deliberately does NOT read
    ///     <c>AgentTelemetryOptions.CaptureSensitiveContent</c>. Three callers — the conversation summarizer, the memory
    ///     extraction agent and the playbook analysis agent — exist to hold a node-boundary invariant on conversation
    ///     content; inheriting the operator's interactive-pipeline opt-in would export exactly the content those call
    ///     sites are built to keep on the node. Setting it explicitly also beats the ambient
    ///     <c>OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT</c>, which Aspire injects as true.
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

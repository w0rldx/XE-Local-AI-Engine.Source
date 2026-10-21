namespace XE_Local_AI_Engine.Providers.CodexOAuth.Implementation;

using Microsoft.Extensions.AI;
using OpenAI.Responses;
using XE_Local_AI_Engine.Providers.OpenAICompatible.Core;

/// <summary>Forces <c>store=false</c> on the Codex Responses transport.</summary>
/// <remarks>
///     The mechanism is the local <see cref="ChatOptions.RawRepresentationFactory" /> rather than a dedicated
///     stored-output-disabling client: the <c>Microsoft.Agents.AI.OpenAI</c> package offering one is not in the repo's
///     package graph, which pins the different <c>Microsoft.Agents.AI.Hosting.OpenAI</c>. This carries no extra
///     dependency and is verified to compile against the pinned OpenAI 2.10.0 / Microsoft.Extensions.AI.OpenAI 10.6.0.
/// </remarks>
public static class CodexResponseStoreDisabling
{
    /// <summary>
    ///     The AdditionalProperties key carrying the RAW normalized reasoning effort for a thinking-capable model on
    ///     the Codex boundary.
    /// </summary>
    /// <remarks>
    ///     Kept in sync with <c>ReasoningOptionsResolver.CodexReasoningEffortKey</c> BY VALUE: the provider is a
    ///     dependency-free leaf and deliberately does not reference the AI.Agent project, so the literal is duplicated
    ///     rather than shared.
    /// </remarks>
    private const string CodexReasoningEffortKey = "codex_reasoning_effort";

    /// <summary>The Ollama-shaped reasoning gate key, shared with the local path.</summary>
    /// <remarks>
    ///     On the Codex boundary it is the FALLBACK source for effort when the richer
    ///     <see cref="CodexReasoningEffortKey" /> side channel is absent: a string value is a graded level,
    ///     <c>false</c> means off, and <c>true</c> means reason at the default effort.
    /// </remarks>
    private const string OllamaThinkKey = "think";

    /// <summary>
    ///     Returns <paramref name="options" />, or a new instance, sending <c>store=false</c>, an auto reasoning summary at
    ///     <paramref name="reasoningEffort" />, the encrypted-reasoning include, and <c>parallel_tool_calls:false</c>.
    /// </summary>
    /// <remarks>
    ///     The shared stateless builder owns the shape; Codex always requests reasoning, and disables parallel tool calls
    ///     because its capability matrix declares <c>SupportsParallelToolCalls=false</c>.
    /// </remarks>
    /// <param name="options">The per-call options to decorate (cloned upstream); a new instance when null.</param>
    /// <param name="reasoningEffort">The resolved per-send effort, or <see langword="null" /> to omit it and use the model's default.</param>
    public static ChatOptions WithStoredOutputDisabled(ChatOptions? options = null,
        ResponseReasoningEffortLevel? reasoningEffort = null)
    {
        return OpenAICompatibleResponsesRequest.Stateless(options, includeReasoning: true, reasoningEffort, parallelToolCalls: false);
    }

    /// <summary>
    ///     Resolves the per-send <see cref="ResponseReasoningEffortLevel" /> from the call's
    ///     <see cref="ChatOptions.AdditionalProperties" />.
    /// </summary>
    /// <remarks>
    ///     Prefers the Codex side channel, which carries full fidelity including minimal and xhigh, and falls back to
    ///     the Ollama <c>think</c> value; <see langword="null" /> when the effort is unspecified, "on" or think:true,
    ///     where the model's default effort applies and summaries are still requested. The pinned OpenAI .NET SDK
    ///     2.10.0 has no <c>XHigh</c> member, so <c>xhigh</c> maps to the nearest supported
    ///     <see cref="ResponseReasoningEffortLevel.High" />.
    /// </remarks>
    public static ResponseReasoningEffortLevel? ResolveReasoningEffort(ChatOptions? options)
    {
        var properties = options?.AdditionalProperties;
        if (properties is null)
        {
            return null;
        }

        if (properties.TryGetValue(CodexReasoningEffortKey, out var rawEffort)
            && rawEffort is string effort
            && MapEffortLevel(effort) is { } mapped)
        {
            return mapped;
        }

        if (properties.TryGetValue(OllamaThinkKey, out var think))
        {
            // think:true is the model's default effort (null), think:false is off (None), a string is a graded level.
            // The None arm is cast so the other arms cannot coerce null through the implicit string operator.
            return think switch
            {
                bool enabled => enabled ? null : (ResponseReasoningEffortLevel?)ResponseReasoningEffortLevel.None,
                string level => MapEffortLevel(level),
                _ => null
            };
        }

        return null;
    }

    /// <summary>
    ///     Maps a canonical effort string to a 2.10.0 <see cref="ResponseReasoningEffortLevel" />. <c>none</c> → None,
    ///     <c>minimal</c> → Minimal, <c>low/medium/high</c> → the matching level, <c>xhigh</c> → High (no XHigh member in
    ///     2.10.0). <c>on</c>, blank, and unrecognized values return <see langword="null" /> (default effort).
    /// </summary>
    private static ResponseReasoningEffortLevel? MapEffortLevel(string? effort)
    {
        if (string.IsNullOrWhiteSpace(effort))
        {
            return null;
        }

        return effort.Trim().ToUpperInvariant() switch
        {
            "NONE" => ResponseReasoningEffortLevel.None,
            "MINIMAL" => ResponseReasoningEffortLevel.Minimal,
            "LOW" => ResponseReasoningEffortLevel.Low,
            "MEDIUM" => ResponseReasoningEffortLevel.Medium,
            "HIGH" => ResponseReasoningEffortLevel.High,
            // 2.10.0 has no XHigh member; degrade to the nearest supported level (High).
            "XHIGH" => ResponseReasoningEffortLevel.High,
            _ => null
        };
    }
}

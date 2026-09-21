namespace XE_Local_AI_Engine.Providers.OpenAICompat.Implementation;

using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Providers.Abstractions.External;

/// <summary>
///     Resolves the turn's reasoning effort for an external model and puts it on the request as MEAI's TYPED
///     <see cref="ChatOptions.Reasoning" />, which the OpenAI chat-completions adapter serializes as the top-level
///     <c>reasoning_effort</c> body field.
/// </summary>
/// <remarks>
///     The typed route is verified against the pinned Microsoft.Extensions.AI.Abstractions 10.9.0,
///     Microsoft.Extensions.AI.OpenAI 10.9.0 and OpenAI 2.12.0, so the raw body patch the llama.cpp path needs is NOT
///     required. Clamping: <c>reasoning_effort</c> is only interoperable at <c>low|medium|high</c> across OpenAI,
///     vLLM, llama.cpp and Groq, so <c>minimal</c> sends <c>low</c> and <c>xhigh</c> sends <c>high</c>, while
///     <c>none</c> and the binary <c>on</c> sentinel send NOTHING — MEAI's <c>None</c> would put a literal on the wire.
/// </remarks>
internal static class ExternalReasoningEffort
{
    /// <summary>
    ///     Returns <paramref name="options" /> with the effective reasoning effort applied, or unchanged when the model
    ///     declares no effort support, no effort resolves, or the resolved effort must send no field.
    /// </summary>
    /// <remarks>The caller's instance is never mutated.</remarks>
    /// <param name="options">The turn's options; may be <see langword="null" />.</param>
    /// <param name="model">The registered model's declarations.</param>
    public static ChatOptions? Apply(ChatOptions? options, ExternalProviderModelDescriptor model)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (!model.SupportsReasoningEffort)
        {
            return options;
        }

        // Precedence: the effort the turn selected beats the model's registered default. A turn that selected nothing
        // falls back to the default, which is what makes "this model always reasons hard" configurable once.
        var selected = ReadSelectedEffort(options) ?? model.DefaultReasoningEffort;
        if (ToWireEffort(selected) is not { } effort)
        {
            return options;
        }

        var patched = options?.Clone() ?? new ChatOptions();
        // A NEW ReasoningOptions rather than a mutation: the clone above is shallow, so writing onto the existing
        // instance would reach back into the caller's options and change a later turn's request too.
        patched.Reasoning = new ReasoningOptions
        {
            Effort = effort,
            Output = patched.Reasoning?.Output
        };
        return patched;
    }

    /// <summary>
    ///     Maps a canonical effort string onto the clamped wire level, or <see langword="null" /> when nothing should be
    ///     sent (unspecified, unrecognized, explicit off, or the binary "reason by default" sentinel).
    /// </summary>
    internal static ReasoningEffort? ToWireEffort(string? effort)
    {
        if (string.IsNullOrWhiteSpace(effort))
        {
            return null;
        }

        // Upper-case for the comparison (CA1308 — never normalize to lower-case); the vocabulary itself is the
        // canonical lowercase set the application-layer reasoning normalizer produces.
        return effort.Trim().ToUpperInvariant() switch
        {
            "MINIMAL" or "LOW" => ReasoningEffort.Low,
            "MEDIUM" => ReasoningEffort.Medium,
            "HIGH" or "XHIGH" => ReasoningEffort.High,
            _ => null
        };
    }

    private static string? ReadSelectedEffort(ChatOptions? options)
    {
        return options?.AdditionalProperties is { } properties
               && properties.TryGetValue<string>(ExternalProviderConstants.ReasoningEffortMarkerKey, out var value)
            ? value
            : null;
    }
}

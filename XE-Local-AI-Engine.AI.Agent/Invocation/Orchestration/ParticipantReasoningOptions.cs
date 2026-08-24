namespace XE_Local_AI_Engine.AI.Agent.Invocation.Orchestration;

using Microsoft.Extensions.AI;

/// <summary>
///     Builds the per-participant reasoning <see cref="AdditionalPropertiesDictionary" /> (the Ollama <c>think</c>
///     option plus the Codex reasoning-effort side channel) an orchestration participant agent must carry on its
///     construction-time <see cref="ChatOptions" />. Workflow-driven participants never receive the outer runner's
///     per-turn <c>RunOptions</c>, so their reasoning has to be baked into the agent when it is built — unlike the
///     single-agent path, where the same contract rides <c>RunOptions.ChatOptions</c>.
///     <para>
///         The capability/effort matrix itself (the Ollama-400 rationale for omitting the field on a non-thinking
///         model, the <c>minimal</c>/<c>xhigh</c> collapse) lives in the shared
///         <see cref="ReasoningOptionsResolver" />, which this and the single-agent path
///         (<c>InvocationAgentFactory.CreateAsync</c>) both call — so the two stay in lockstep by construction rather
///         than by hand-applied edits.
///     </para>
/// </summary>
internal static class ParticipantReasoningOptions
{
    /// <summary>Forwards to <see cref="ReasoningOptionsResolver.CodexReasoningEffortKey" />; kept for callers already using this name.</summary>
    internal const string CodexReasoningEffortKey = ReasoningOptionsResolver.CodexReasoningEffortKey;

    /// <summary>Forwards to <see cref="ReasoningOptionsResolver.LlamaReasoningBudgetMarkerKey" />.</summary>
    internal const string LlamaReasoningBudgetMarkerKey = ReasoningOptionsResolver.LlamaReasoningBudgetMarkerKey;

    /// <summary>
    ///     Produces the reasoning properties for a participant, gated on its resolved model's thinking capability:
    ///     <list type="bullet">
    ///         <item>thinking-capable → <c>think</c> = false/low/medium/high/true, plus the Codex side channel for a graded effort;</item>
    ///         <item>non-thinking + reasoning requested → OMIT <c>think</c> (an empty dictionary) so the model's built-in reasoning runs (Ollama 400s on <c>think:true</c>/level);</item>
    ///         <item>non-thinking + reasoning off/unspecified → <c>think</c> = false.</item>
    ///     </list>
    ///     Always returns a dictionary (never null) — mirroring the single-agent path, which always assigns the built
    ///     dictionary to <see cref="ChatOptions.AdditionalProperties" /> even when it is empty.
    /// </summary>
    internal static AdditionalPropertiesDictionary Build(string? reasoningEffort, bool supportsThinking)
    {
        var properties = new AdditionalPropertiesDictionary();

        if (supportsThinking)
        {
            // Graded reasoning model: honor the requested effort (false / "low" / "medium" / "high"). minimal/xhigh
            // collapse to think:true here because Ollama 400s on an unknown think level (see ResolveThinkOption).
            properties["think"] = ReasoningOptionsResolver.ResolveThinkOption(reasoningEffort);

            var codexEffort = ReasoningOptionsResolver.ResolveCodexReasoningEffort(reasoningEffort);
            if (codexEffort is not null)
            {
                properties[ReasoningOptionsResolver.CodexReasoningEffortKey] = codexEffort;
            }

            // Per-request thinking budget for the llama.cpp path, mirroring the single-agent factory: an explicit graded
            // effort caps the reasoning so a participant cannot burn its whole window thinking and answer nothing. An
            // unspecified effort resolves to null and adds no entry.
            if (ReasoningOptionsResolver.ResolveReasoningBudgetTokens(reasoningEffort) is { } budgetTokens)
            {
                properties[LlamaReasoningBudgetMarkerKey] = budgetTokens;
            }
        }
        else if (ReasoningOptionsResolver.IsReasoningRequested(reasoningEffort))
        {
            // Non-thinking model, reasoning requested: OMIT the think field so the model's default (chat-template-baked)
            // reasoning is allowed through. Sending think:true or a level returns HTTP 400 for a model without the
            // thinking capability, so the key is intentionally left out (the dictionary stays empty).
        }
        else
        {
            // Non-thinking model, reasoning OFF ("none") or unspecified: think:false actively suppresses the reasoning
            // some GGUF templates emit by default.
            properties["think"] = false;
        }

        return properties;
    }
}

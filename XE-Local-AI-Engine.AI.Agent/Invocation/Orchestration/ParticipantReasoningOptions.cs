namespace XE_Local_AI_Engine.AI.Agent.Invocation.Orchestration;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

/// <summary>
///     Builds the per-participant reasoning <see cref="AdditionalPropertiesDictionary" /> an orchestration participant
///     agent must carry on its construction-time <see cref="ChatOptions" />.
/// </summary>
/// <remarks>
///     Workflow-driven participants never receive the outer runner's per-turn <c>RunOptions</c>, so their reasoning has
///     to be baked in at build time, unlike the single-agent path where the same contract rides
///     <c>RunOptions.ChatOptions</c>. The capability/effort matrix itself lives in the shared
///     <see cref="ReasoningOptionsResolver" />, which both paths call, so the two stay in lockstep by construction.
/// </remarks>
internal static class ParticipantReasoningOptions
{
    /// <summary>Forwards to <see cref="ReasoningOptionsResolver.CodexReasoningEffortKey" />; kept for callers already using this name.</summary>
    internal const string CodexReasoningEffortKey = ReasoningOptionsResolver.CodexReasoningEffortKey;

    /// <summary>Forwards to <see cref="ReasoningOptionsResolver.LlamaReasoningBudgetMarkerKey" />.</summary>
    internal const string LlamaReasoningBudgetMarkerKey = ReasoningOptionsResolver.LlamaReasoningBudgetMarkerKey;

    /// <summary>Forwards to <see cref="ReasoningOptionsResolver.ExternalReasoningEffortMarkerKey" />.</summary>
    internal const string ExternalReasoningEffortMarkerKey = ReasoningOptionsResolver.ExternalReasoningEffortMarkerKey;

    /// <summary>
    ///     Produces the reasoning properties for a participant, gated on its resolved model's thinking capability.
    /// </summary>
    /// <remarks>
    ///     Thinking-capable gets a graded <c>think</c> value plus the Codex, external and llama-budget side channels;
    ///     a non-thinking model with reasoning requested gets an EMPTY dictionary, omitting <c>think</c> so its
    ///     built-in reasoning runs; otherwise <c>think</c> is false. Always returns a dictionary, never null, mirroring
    ///     the single-agent path. See docs/wiki/04-agent-mode.md ("The reasoning-effort matrix and the thinking budget").
    /// </remarks>
    /// <param name="reasoningBudgetEnforceable">
    ///     Whether llama-server can ENFORCE a <c>reasoning_budget_tokens</c> here; false omits the marker.
    /// </param>
    /// <param name="logger">Receives the one-per-model skip notice; omit it to skip silently (no other logging).</param>
    /// <param name="modelId">The participant's resolved model id, the de-duplication key for that notice.</param>
    internal static AdditionalPropertiesDictionary Build(string? reasoningEffort,
        bool supportsThinking,
        bool reasoningBudgetEnforceable = true,
        ILogger? logger = null,
        string? modelId = null)
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

            // External-provider side channel, mirroring the single-agent factory. Absent for every other model, and for
            // an unspecified effort — which is what lets the model's registered default apply instead.
            var externalEffort = ReasoningOptionsResolver.ResolveExternalReasoningEffort(modelId, reasoningEffort);
            if (externalEffort is not null)
            {
                properties[ExternalReasoningEffortMarkerKey] = externalEffort;
            }

            // Per-request thinking budget for the llama.cpp path, so a participant cannot burn its whole window
            // thinking and answer nothing. Skipped where llama.cpp cannot enforce it, since it would advertise no cap.
            if (ReasoningOptionsResolver.ResolveReasoningBudgetTokens(reasoningEffort) is { } budgetTokens)
            {
                if (reasoningBudgetEnforceable)
                {
                    properties[LlamaReasoningBudgetMarkerKey] = budgetTokens;
                }
                else if (logger is not null)
                {
                    ReasoningBudgetSkipLog.ReportBudgetSkipped(logger, modelId);
                }
            }
        }
        else if (ReasoningOptionsResolver.IsReasoningRequested(reasoningEffort))
        {
            // Non-thinking model, reasoning requested: OMIT the think field so chat-template-baked reasoning runs.
            // Sending think:true or a level returns HTTP 400 without the capability, so the dictionary stays empty.
        }
        else
        {
            // Non-thinking model, reasoning OFF or unspecified: think:false actively suppresses the reasoning some
            // GGUF templates emit by default.
            properties["think"] = false;
        }

        return properties;
    }
}

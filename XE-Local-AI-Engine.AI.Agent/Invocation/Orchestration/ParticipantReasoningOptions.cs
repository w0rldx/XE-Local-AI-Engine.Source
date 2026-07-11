namespace XE_Local_AI_Engine.AI.Agent.Invocation.Orchestration;

using Microsoft.Extensions.AI;

/// <summary>
///     Builds the per-participant reasoning <see cref="AdditionalPropertiesDictionary" /> (the Ollama <c>think</c>
///     option plus the Codex reasoning-effort side channel) an orchestration participant agent must carry on its
///     construction-time <see cref="ChatOptions" />. Workflow-driven participants never receive the outer runner's
///     per-turn <c>RunOptions</c>, so their reasoning has to be baked into the agent when it is built — unlike the
///     single-agent path, where the same contract rides <c>RunOptions.ChatOptions</c>.
///     <para>
///         This intentionally MIRRORS (does not reuse) the single-agent think contract implemented inline in
///         <c>InvocationAgentFactory.CreateAsync</c>: that factory owns its own copy and this helper lives in the
///         .AI.Agent assembly so the orchestration factory can honor per-participant reasoning without depending on it.
///         The two must stay in lockstep — the capability/effort matrix, the Ollama-400 rationale for omitting the
///         field on a non-thinking model, and the <c>minimal</c>/<c>xhigh</c> collapse are documented there. Any change
///         to one must be applied to the other.
///     </para>
/// </summary>
internal static class ParticipantReasoningOptions
{
    /// <summary>
    ///     Codex-only side channel carrying the RAW normalized reasoning-effort string. Kept byte-identical to
    ///     <c>InvocationAgentFactory.CodexReasoningEffortKey</c> (they intentionally duplicate the same wire key across
    ///     the two assemblies); see that field for the full rationale.
    /// </summary>
    internal const string CodexReasoningEffortKey = "codex_reasoning_effort";

    /// <summary>The binary reasoning-"on" sentinel; mirrors <c>InvocationAgentFactory.BinaryReasoningOn</c>.</summary>
    private const string BinaryReasoningOn = "on";

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
            properties["think"] = ResolveThinkOption(reasoningEffort);

            var codexEffort = ResolveCodexReasoningEffort(reasoningEffort);
            if (codexEffort is not null)
            {
                properties[CodexReasoningEffortKey] = codexEffort;
            }
        }
        else if (IsReasoningRequested(reasoningEffort))
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

    private static object ResolveThinkOption(string? reasoningEffort)
    {
        if (string.IsNullOrWhiteSpace(reasoningEffort))
        {
            return true;
        }

        var normalized = reasoningEffort.Trim();
        if (string.Equals(normalized, "low", StringComparison.OrdinalIgnoreCase))
        {
            return "low";
        }

        if (string.Equals(normalized, "none", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(normalized, "medium", StringComparison.OrdinalIgnoreCase))
        {
            return "medium";
        }

        if (string.Equals(normalized, "high", StringComparison.OrdinalIgnoreCase))
        {
            return "high";
        }

        // minimal / xhigh are Codex (OpenAI Responses) levels Ollama does NOT understand — Ollama rejects an unknown
        // think level with HTTP 400 — so they collapse to a boolean true think value here, keeping the Ollama wire
        // safe while the Codex boundary reads the un-collapsed level from the CodexReasoningEffortKey side channel.
        // Also covers the "on" binary-reasoning sentinel defensively.
        return true;
    }

    private static string? ResolveCodexReasoningEffort(string? reasoningEffort)
    {
        if (string.IsNullOrWhiteSpace(reasoningEffort))
        {
            return null;
        }

        return reasoningEffort.Trim().ToUpperInvariant() switch
        {
            "NONE" => "none",
            "MINIMAL" => "minimal",
            "LOW" => "low",
            "MEDIUM" => "medium",
            "HIGH" => "high",
            "XHIGH" => "xhigh",
            _ => null
        };
    }

    private static bool IsReasoningRequested(string? reasoningEffort)
    {
        if (string.IsNullOrWhiteSpace(reasoningEffort))
        {
            return false;
        }

        var normalized = reasoningEffort.Trim();
        return string.Equals(normalized, BinaryReasoningOn, StringComparison.OrdinalIgnoreCase)
               || string.Equals(normalized, "low", StringComparison.OrdinalIgnoreCase)
               || string.Equals(normalized, "medium", StringComparison.OrdinalIgnoreCase)
               || string.Equals(normalized, "high", StringComparison.OrdinalIgnoreCase);
    }
}

namespace XE_Local_AI_Engine.Providers.CodexOAuth;

using Microsoft.Extensions.AI;
using OpenAI.Responses;

/// <summary>
/// Forces <c>store=false</c> on the Codex Responses transport (plan D10 / M1 / pre-mortem §B-3).
///
/// <para>
/// <b>Phase 1.7 store=false decision (compile-gated):</b> the plan prefers
/// <c>ResponsesClient.AsIChatClientWithStoredOutputDisabled()</c> ONLY IF <c>Microsoft.Agents.AI.OpenAI</c>
/// compiles cleanly with the repo's existing <c>Microsoft.Agents.AI*</c> 1.6.2 package set. That package is
/// NOT present in the repo's package graph (the repo pins <c>Microsoft.Agents.AI.Hosting.OpenAI</c>, a
/// different package) and is not resolvable, so per the plan default we use the local
/// <see cref="ChatOptions.RawRepresentationFactory"/> mechanism instead. This carries no extra dependency and
/// is verified to compile against the pinned OpenAI 2.10.0 / Microsoft.Extensions.AI.OpenAI 10.6.0.
/// </para>
///
/// <para>
/// MEAI's Responses mapper uses the object returned by <see cref="ChatOptions.RawRepresentationFactory"/> as
/// the base <see cref="CreateResponseOptions"/>. Setting <see cref="CreateResponseOptions.StoredOutputEnabled"/>
/// to <see langword="false"/> and leaving <see cref="CreateResponseOptions.PreviousResponseId"/> /
/// <see cref="CreateResponseOptions.ConversationOptions"/> unset yields a request body that omits service-side
/// state. A Phase-3 body-assertion test proves the emitted body matches.
/// </para>
///
/// <para>
/// <b>Reasoning summaries (2026-06-08):</b> the same base options also opt the request into OpenAI Responses
/// reasoning summaries — <see cref="CreateResponseOptions.ReasoningOptions"/> with
/// <see cref="ResponseReasoningSummaryVerbosity.Auto"/> (decision D2, fixed) and the per-send
/// <see cref="ResponseReasoningEffortLevel"/> mapped from the chat reasoning effort. Summaries ride the response
/// output and do NOT require <c>store=true</c>, so the store=false invariant above is preserved. When no effort is
/// resolvable the request still asks for summaries at the model's default effort (effort omitted).
/// </para>
///
/// <para>
/// <b>Encrypted reasoning include (tool calling, de-risk plan <c>Plans/2026-06-08-codex-tool-calling-derisk.md</c>
/// D3):</b> because reasoning is always requested on the Codex boundary, the same base options also add
/// <see cref="IncludedResponseProperty.ReasoningEncryptedContent"/> to
/// <see cref="CreateResponseOptions.IncludedProperties"/> (serializes to <c>include:[reasoning.encrypted_content]</c>).
/// This is REQUIRED for the stateless tool loop: with <c>store=false</c> each follow-up turn must replay the prior
/// reasoning item with its <c>encrypted_content</c> immediately before the <c>function_call</c> it produced. The
/// include makes the backend emit that encrypted blob; MEAI then round-trips it verbatim. It is harmless when no tool
/// is offered, so it rides whenever reasoning is on. Tools themselves are NOT stripped on this boundary.
/// </para>
/// </summary>
public static class CodexResponseStoreDisabling
{
    /// <summary>
    /// Returns <paramref name="options"/> (or a new instance) with a <see cref="ChatOptions.RawRepresentationFactory"/>
    /// that disables service-side stored output AND requests reasoning summaries (verbosity Auto) at the supplied
    /// <paramref name="reasoningEffort"/> for the Codex Responses path.
    /// </summary>
    /// <param name="options">The per-call options to decorate (cloned upstream); a new instance when null.</param>
    /// <param name="reasoningEffort">
    /// The resolved per-send <see cref="ResponseReasoningEffortLevel"/>, or <see langword="null"/> to omit the effort
    /// (the model's default effort applies, summaries still requested).
    /// </param>
    public static ChatOptions WithStoredOutputDisabled(
        ChatOptions? options = null,
        ResponseReasoningEffortLevel? reasoningEffort = null)
    {
        var result = options ?? new ChatOptions();
        result.RawRepresentationFactory = _ =>
        {
            var responseOptions = new CreateResponseOptions
            {
                StoredOutputEnabled = false,
                ReasoningOptions = new ResponseReasoningOptions
                {
                    // D2: summary verbosity is FIXED to Auto (≈ detailed for gpt-5.x). Requesting summaries is what
                    // makes reasoning text flow back as TextReasoningContent for the React reasoning pipeline.
                    ReasoningSummaryVerbosity = ResponseReasoningSummaryVerbosity.Auto,
                },
                // D3: reasoning is always on here, so always ask the backend to emit the encrypted reasoning blob
                // (include:[reasoning.encrypted_content]). Required for the stateless (store=false) tool loop — each
                // follow-up turn replays the prior reasoning item with its encrypted_content before its function_call.
                // Harmless when no tool is offered.
                IncludedProperties = { IncludedResponseProperty.ReasoningEncryptedContent },

                // D2 (single-call first): disable parallel tool calls on the wire (serializes parallel_tool_calls:false).
                // The Codex capability matrix declares SupportsParallelToolCalls=false; this is the request-level
                // enforcement of that decision so the model emits at most one tool call per turn. Harmless when no tool
                // is offered.
                ParallelToolCallsEnabled = false,
            };

            if (reasoningEffort is { } effort)
            {
                responseOptions.ReasoningOptions.ReasoningEffortLevel = effort;
            }

            return responseOptions;
        };
        return result;
    }

    /// <summary>
    /// The AdditionalProperties key the agent factory uses to carry the RAW normalized reasoning effort
    /// (minimal/low/medium/high/xhigh/none) for a thinking-capable model on the Codex boundary. Kept in sync with
    /// <c>InvocationAgentFactory.CodexReasoningEffortKey</c> by value — the provider is a dependency-free leaf and
    /// deliberately does not reference the AI.Agent project, so the literal is duplicated, not shared.
    /// </summary>
    private const string CodexReasoningEffortKey = "codex_reasoning_effort";

    /// <summary>
    /// The Ollama-shaped reasoning gate key (shared with the local path). On the Codex boundary it is the FALLBACK
    /// source for effort when the richer <see cref="CodexReasoningEffortKey"/> side channel is absent: a string value
    /// is a graded level (low/medium/high), <c>false</c> means off, and <c>true</c> means reason at the default effort.
    /// </summary>
    private const string OllamaThinkKey = "think";

    /// <summary>
    /// Resolves the per-send <see cref="ResponseReasoningEffortLevel"/> from the call's
    /// <see cref="ChatOptions.AdditionalProperties"/>. Prefers the Codex side-channel (full fidelity, incl.
    /// minimal/xhigh) and falls back to the Ollama <c>think</c> value. Returns <see langword="null"/> when the effort
    /// is unspecified/"on"/think:true (the model's default effort applies, summaries still requested).
    /// <para>
    /// <b>xhigh fallback:</b> the pinned OpenAI .NET SDK 2.10.0 exposes None/Minimal/Low/Medium/High but has no
    /// <c>XHigh</c> member, so <c>xhigh</c> maps to the nearest supported level, <see cref="ResponseReasoningEffortLevel.High"/>.
    /// </para>
    /// </summary>
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
            // think:true ≡ reason at the model's default effort (omit effort → null). think:false ≡ off (None). A string
            // value is a graded level. The None arm is cast to the nullable struct so the other arms don't coerce a null
            // through ResponseReasoningEffortLevel's implicit string operator (which throws on a null string).
            return think switch
            {
                bool enabled => enabled ? null : (ResponseReasoningEffortLevel?)ResponseReasoningEffortLevel.None,
                string level => MapEffortLevel(level),
                _ => null,
            };
        }

        return null;
    }

    /// <summary>
    /// Maps a canonical effort string to a 2.10.0 <see cref="ResponseReasoningEffortLevel"/>. <c>none</c> → None,
    /// <c>minimal</c> → Minimal, <c>low/medium/high</c> → the matching level, <c>xhigh</c> → High (no XHigh member in
    /// 2.10.0). <c>on</c>, blank, and unrecognized values return <see langword="null"/> (default effort).
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
            _ => null,
        };
    }
}

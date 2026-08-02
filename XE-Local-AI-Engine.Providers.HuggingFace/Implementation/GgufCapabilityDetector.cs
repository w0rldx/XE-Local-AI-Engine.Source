namespace XE_Local_AI_Engine.Providers.HuggingFace.Implementation;

/// <summary>
///     Deterministically classifies a GGUF model's tool / reasoning surface from its embedded Jinja chat template
///     (<c>tokenizer.chat_template</c> in the GGUF header). The template is the most reliable offline signal — a model
///     wired for tool calling renders the tool list in its template, and a model that exposes a thinking channel renders
///     the think markers — so we never need an Ollama <c>/api/show</c> probe (a GGUF has no Ollama entry, and desktop
///     mode runs no Ollama daemon). Stateless and pure: the same template always yields the same classification.
/// </summary>
/// <remarks>
///     The heuristics are intentionally conservative — a false negative merely hides an available capability (the model
///     still chats), while a false positive would offer tools the model cannot honor. The token sets below are matched
///     case-insensitively against the raw template text and cover the mainstream tool/reasoning template families
///     (Qwen2.5/Qwen3, Llama 3.1+, Mistral/Hermes, DeepSeek-R1, OpenAI harmony).
///     Reasoning is TWO distinct capabilities, not one:
///     <list type="bullet">
///         <item>
///             GRADED (<c>thinking</c>) — the template exposes a switchable thinking channel, so a
///             <c>think:&lt;level&gt;</c> control is genuinely available.
///         </item>
///         <item>
///             NATIVE (<c>native_reasoning</c>) — the model reasons on a channel baked into its template with no such
///             switch. Detecting it as its own capability is what stops a reasoning model being advertised as unable to
///             reason, WITHOUT handing it a graded control whose levels would do nothing.
///         </item>
///     </list>
/// </remarks>
internal static class GgufCapabilityDetector
{
    // Ollama-style capability tokens reused across the system so a GGUF entry classifies identically to an Ollama one.
    private const string CompletionCapability = "completion";
    private const string ToolsCapability = "tools";
    private const string ThinkingCapability = "thinking";

    /// <summary>
    ///     The SECOND, distinct reasoning capability: the model reasons on a channel its chat template bakes in, with no
    ///     graded <c>think:&lt;level&gt;</c> switch to drive it. Deliberately NOT the Ollama <c>thinking</c> token —
    ///     <c>ModelKindDetector.SupportsThinking</c> matches that token by EXACT equality, so this one can never flip a
    ///     native-reasoning model into the graded branch (which would write <c>think</c> and, on effort <c>none</c>, an
    ///     <c>enable_thinking=false</c> the harmony template has no kwarg for). See <see cref="NativeReasoningTemplateMarkers" />.
    /// </summary>
    private const string NativeReasoningCapability = "native_reasoning";

    // A tool-templated model references the tool collection and/or the tool-call message shape. Qwen2.5, Llama 3.1+,
    // Mistral, and Hermes templates all iterate `tools` and emit `tool_call`/`tool_calls`/`function_call`.
    private static readonly string[] ToolTemplateMarkers =
    [
        "tool_calls",
        "tool_call",
        "function_call",
        "tools"
    ];

    // A GRADED-reasoning model carries an explicit thinking channel the caller can switch: the `<think>` marker (Qwen3,
    // DeepSeek-R1), the Qwen3 `enable_thinking` switch, or the `reasoning_content` field the template branches on. A
    // match here means a `think:<level>` control is available, so the model takes the graded branch downstream.
    private static readonly string[] ReasoningTemplateMarkers =
    [
        "<think",
        "enable_thinking",
        "reasoning_content"
    ];

    // A NATIVE-reasoning model reasons on a channel baked into its template, with no switch the caller can grade. The
    // OpenAI harmony family (gpt-oss) is the reference case: reasoning is emitted on `<|channel|>analysis<|message|>`
    // and the only knob is the `reasoning_effort` string the template renders into the system prompt itself.
    //
    // Both markers are literal template tags, NOT loose English words — verified by counting them in the genuine
    // `tokenizer.chat_template` of unsloth/gpt-oss-20b-GGUF:Q5_K_M (2026-07-31): `<|channel|>analysis` ×5,
    // `reasoning_effort` ×4, while `<think` / `enable_thinking` / `reasoning_content` are all ×0 — which is exactly why
    // the graded list above cannot see this family. Keep these literal: a bare word like "analysis" or "reasoning"
    // would match prose in unrelated templates and mislabel them.
    private static readonly string[] NativeReasoningTemplateMarkers =
    [
        "<|channel|>analysis",
        "reasoning_effort"
    ];

    /// <summary>
    ///     Classifies the supplied chat template into <c>(IsToolCapable, IsReasoningCapable, IsNativeReasoningCapable)</c>
    ///     plus the matching Ollama-style capability tokens. A null/blank template (a raw base model, or a header read
    ///     that did not reach the template) yields the safe default — chat-only, no tools, no reasoning.
    /// </summary>
    /// <remarks>
    ///     The two reasoning flags are MUTUALLY EXCLUSIVE and graded wins. A graded template already advertises a
    ///     switchable thinking channel, so re-reporting it as "native" would render two chips meaning the same thing;
    ///     more importantly, the exclusivity keeps the native flag a pure "reasons, but ungraded" signal that downstream
    ///     code can trust without re-deriving it.
    /// </remarks>
    public static GgufCapabilities Detect(string? chatTemplate)
    {
        // Every installed GGUF in the chat picker has a completion head by construction, so completion is always present
        // even when the template is absent. Tool/reasoning are added only on a positive template match.
        var capabilities = new List<string>(3)
        {
            CompletionCapability
        };

        if (string.IsNullOrWhiteSpace(chatTemplate))
        {
            return new GgufCapabilities(IsToolCapable: false, IsReasoningCapable: false, IsNativeReasoningCapable: false, capabilities);
        }

        var isToolCapable = ContainsAny(chatTemplate, ToolTemplateMarkers);
        var isReasoningCapable = ContainsAny(chatTemplate, ReasoningTemplateMarkers);

        // Native reasoning is only considered when the graded markers did NOT match (see the remarks above).
        var isNativeReasoningCapable = !isReasoningCapable && ContainsAny(chatTemplate, NativeReasoningTemplateMarkers);

        if (isToolCapable)
        {
            capabilities.Add(ToolsCapability);
        }

        if (isReasoningCapable)
        {
            capabilities.Add(ThinkingCapability);
        }

        if (isNativeReasoningCapable)
        {
            capabilities.Add(NativeReasoningCapability);
        }

        return new GgufCapabilities(isToolCapable, isReasoningCapable, isNativeReasoningCapable, capabilities);
    }

    private static bool ContainsAny(string text, string[] markers)
    {
        return markers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
///     The capability classification of a single GGUF model derived from its chat template:
///     <paramref name="IsToolCapable" /> / <paramref name="IsReasoningCapable" /> /
///     <paramref name="IsNativeReasoningCapable" /> flags plus the matching Ollama-style capability tokens (always
///     includes <c>completion</c>).
/// </summary>
/// <param name="IsReasoningCapable">
///     GRADED reasoning: the template exposes a switchable thinking channel, so a <c>think:&lt;level&gt;</c> control is
///     available. Mutually exclusive with <paramref name="IsNativeReasoningCapable" />.
/// </param>
/// <param name="IsNativeReasoningCapable">
///     NATIVE reasoning: the model reasons on a channel baked into its template with no graded switch (harmony/gpt-oss).
///     It must stay OUT of the graded path — the enforcing layer keeps such a model on the omit-<c>think</c> branch.
/// </param>
internal readonly record struct GgufCapabilities(
    bool IsToolCapable,
    bool IsReasoningCapable,
    bool IsNativeReasoningCapable,
    IReadOnlyList<string> Capabilities);

namespace XE_Local_AI_Engine.Providers.HuggingFace;

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
///     (Qwen2.5/Qwen3, Llama 3.1+, Mistral/Hermes, DeepSeek-R1).
/// </remarks>
internal static class GgufCapabilityDetector
{
    // Ollama-style capability tokens reused across the system so a GGUF entry classifies identically to an Ollama one.
    private const string CompletionCapability = "completion";
    private const string ToolsCapability = "tools";
    private const string ThinkingCapability = "thinking";

    // A tool-templated model references the tool collection and/or the tool-call message shape. Qwen2.5, Llama 3.1+,
    // Mistral, and Hermes templates all iterate `tools` and emit `tool_call`/`tool_calls`/`function_call`.
    private static readonly string[] ToolTemplateMarkers =
    [
        "tool_calls",
        "tool_call",
        "function_call",
        "tools"
    ];

    // A reasoning-templated model carries an explicit thinking channel: the `<think>` marker (Qwen3, DeepSeek-R1),
    // the Qwen3 `enable_thinking` switch, or the `reasoning_content` field the template branches on.
    private static readonly string[] ReasoningTemplateMarkers =
    [
        "<think",
        "enable_thinking",
        "reasoning_content"
    ];

    /// <summary>
    ///     Classifies the supplied chat template into <c>(IsToolCapable, IsReasoningCapable)</c> plus the matching
    ///     Ollama-style capability tokens. A null/blank template (a raw base model, or a header read that did not reach
    ///     the template) yields the safe default — chat-only, no tools, no reasoning.
    /// </summary>
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
            return new GgufCapabilities(IsToolCapable: false, IsReasoningCapable: false, capabilities);
        }

        var isToolCapable = ContainsAny(chatTemplate, ToolTemplateMarkers);
        var isReasoningCapable = ContainsAny(chatTemplate, ReasoningTemplateMarkers);

        if (isToolCapable)
        {
            capabilities.Add(ToolsCapability);
        }

        if (isReasoningCapable)
        {
            capabilities.Add(ThinkingCapability);
        }

        return new GgufCapabilities(isToolCapable, isReasoningCapable, capabilities);
    }

    private static bool ContainsAny(string text, string[] markers)
    {
        return markers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
///     The capability classification of a single GGUF model derived from its chat template:
///     <paramref name="IsToolCapable" /> / <paramref name="IsReasoningCapable" /> flags plus the matching Ollama-style
///     capability tokens (always includes <c>completion</c>).
/// </summary>
internal readonly record struct GgufCapabilities(bool IsToolCapable, bool IsReasoningCapable, IReadOnlyList<string> Capabilities);

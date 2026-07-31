namespace XE_Local_AI_Engine.Tests.Providers.HuggingFace;

using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Providers.HuggingFace.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="GgufCapabilityDetector" />: deterministically classifies a GGUF model's tool / reasoning surface from
///     its embedded Jinja chat template. A tool-templated model is tool-capable, a thinking-channel template is
///     reasoning-capable, and a plain template (or no template) is neither — the safe default that never offers tools a
///     model cannot honor.
///     Reasoning is TWO distinct capabilities: GRADED (a switchable <c>think:&lt;level&gt;</c> control) and NATIVE (the
///     model reasons on a template-baked channel with no switch — the OpenAI harmony family).
/// </summary>
public sealed class GgufCapabilityDetectorTests
{
    // A trimmed but representative Qwen2.5 template fragment — it iterates `tools` and emits the tool_call message.
    private const string Qwen25ToolTemplate =
        "{%- if tools %}{{- '<|im_start|>system\\n' }}{%- for tool in tools %}{{- tool | tojson }}{%- endfor %}"
        + "{%- endif %}{%- for message in messages %}{%- if message.tool_calls %}<tool_call>{%- endif %}{%- endfor %}";

    // A Qwen3-style template that exposes a thinking channel.
    private const string Qwen3ReasoningTemplate =
        "{%- if enable_thinking %}<think>\\n{%- endif %}{%- if message.reasoning_content %}{{ message.reasoning_content }}{%- endif %}";

    // A plain chat template with neither tools nor a thinking channel.
    private const string PlainChatTemplate =
        "{% for message in messages %}<|im_start|>{{ message.role }}\\n{{ message.content }}<|im_end|>\\n{% endfor %}";

    /// <summary>
    ///     A verbatim excerpt of the OpenAI harmony chat template, copied from the genuine
    ///     <c>tokenizer.chat_template</c> in the GGUF header of <c>unsloth/gpt-oss-20b-GGUF:Q5_K_M</c> (read 2026-07-31).
    ///     This is the template that made gpt-oss-20b advertise NO reasoning capability while reasoning perfectly well
    ///     live: measured over the full 17 221-character template, <c>&lt;think</c>, <c>enable_thinking</c> and
    ///     <c>reasoning_content</c> each occur ZERO times, while <c>&lt;|channel|&gt;analysis</c> occurs 5× and
    ///     <c>reasoning_effort</c> 4×. The excerpt below preserves that property exactly — both native markers present,
    ///     no graded marker anywhere — so it exercises the real detection split rather than a synthetic one.
    /// </summary>
    private const string HarmonyNativeReasoningTemplate =
        """
        {{- model_identity + "\n" }}
        {{- "Knowledge cutoff: 2024-06\n" }}
        {%- if reasoning_effort is not defined %}
            {%- set reasoning_effort = "medium" %}
        {%- endif %}
        {{- "Reasoning: " + reasoning_effort + "\n\n" }}
        {%- if "<|channel|>analysis<|message|>" in message.thinking %}
            {{- "<|start|>assistant<|channel|>analysis<|message|>" + message.thinking + "<|end|>" }}
        {%- endif %}
        {{- "<|start|>assistant<|channel|>final<|message|>" + message.content + "<|return|>" }}
        """;

    [Test]
    public void Detect_ToolTemplate_IsToolCapable()
    {
        var result = GgufCapabilityDetector.Detect(Qwen25ToolTemplate);

        AssertEx.True(result.IsToolCapable, "a template that iterates tools / emits tool_call is tool-capable");
        AssertEx.Contains(result.Capabilities, static c => c == "tools");
        AssertEx.Contains(result.Capabilities, static c => c == "completion");
    }

    [Test]
    public void Detect_ReasoningTemplate_IsReasoningCapable()
    {
        var result = GgufCapabilityDetector.Detect(Qwen3ReasoningTemplate);

        AssertEx.True(result.IsReasoningCapable, "a template with a <think>/enable_thinking channel is reasoning-capable");
        AssertEx.Contains(result.Capabilities, static c => c == "thinking");
    }

    [Test]
    public void Detect_GradedReasoningTemplate_IsNotAlsoNativeReasoningCapable()
    {
        var result = GgufCapabilityDetector.Detect(Qwen3ReasoningTemplate);

        // The two reasoning capabilities are mutually exclusive and graded wins, so a Qwen3-class model renders ONE
        // reasoning chip, not two saying the same thing.
        AssertEx.False(result.IsNativeReasoningCapable, "a graded thinking template must not also report native reasoning");
        AssertEx.False(result.Capabilities.Any(static c => c == "native_reasoning"), "no native_reasoning token on a graded template");
    }

    /// <summary>
    ///     F-014: the harmony template reasons on its own channel, so the model MUST be reported reasoning-capable —
    ///     but as the NATIVE capability, never the graded one. Flipping the graded flag here would route gpt-oss into
    ///     the <c>think</c>-writing branch and, on effort <c>none</c>, send an <c>enable_thinking=false</c> the harmony
    ///     template has no kwarg for.
    /// </summary>
    [Test]
    public void Detect_HarmonyTemplate_IsNativeReasoningCapableButNotGraded()
    {
        var result = GgufCapabilityDetector.Detect(HarmonyNativeReasoningTemplate);

        AssertEx.True(result.IsNativeReasoningCapable, "the harmony <|channel|>analysis template reasons natively");
        AssertEx.Contains(result.Capabilities, static c => c == "native_reasoning");

        AssertEx.False(result.IsReasoningCapable,
            "harmony carries no <think>/enable_thinking/reasoning_content marker, so it must NOT report graded reasoning");
        AssertEx.False(result.Capabilities.Any(static c => c == "thinking"),
            "the graded `thinking` token must never be emitted for a harmony template");
    }

    /// <summary>
    ///     The regression that would break real inference. <c>ModelKindDetector.SupportsThinking</c> is the seam that
    ///     decides whether the invocation factory writes the <c>think</c> field: it matches the <c>thinking</c> token by
    ///     EXACT equality, so the deliberately distinct <c>native_reasoning</c> token can never flip a harmony model into
    ///     the graded branch. Pinned here on the detector's real output, not a hand-built capability list.
    /// </summary>
    [Test]
    public void Detect_HarmonyTemplate_DoesNotResolveAsThinkingCapableDownstream()
    {
        var result = GgufCapabilityDetector.Detect(HarmonyNativeReasoningTemplate);

        AssertEx.False(ModelKindDetector.SupportsThinking(result.Capabilities),
            "a native-reasoning model must resolve SupportsThinking=false so the factory keeps it on the omit-think path");
    }

    [Test]
    public void Detect_PlainTemplate_IsNeitherToolNorReasoningCapable()
    {
        var result = GgufCapabilityDetector.Detect(PlainChatTemplate);

        AssertEx.False(result.IsToolCapable, "a plain template offers no tools");
        AssertEx.False(result.IsReasoningCapable, "a plain template exposes no thinking channel");
        AssertEx.False(result.IsNativeReasoningCapable, "a plain template exposes no native reasoning channel either");
        // Completion is always present (a chat model has a completion head), but no tool/thinking token leaks in.
        AssertEx.Equal(expected: 1, result.Capabilities.Count);
        AssertEx.Contains(result.Capabilities, static c => c == "completion");
    }

    [Test]
    public void Detect_NullOrBlankTemplate_FallsBackToCompletionOnly()
    {
        var fromNull = GgufCapabilityDetector.Detect(null);
        var fromBlank = GgufCapabilityDetector.Detect("   ");

        AssertEx.False(fromNull.IsToolCapable);
        AssertEx.False(fromNull.IsReasoningCapable);
        AssertEx.False(fromNull.IsNativeReasoningCapable);
        AssertEx.Equal(expected: 1, fromNull.Capabilities.Count);
        AssertEx.Contains(fromNull.Capabilities, static c => c == "completion");
        AssertEx.False(fromBlank.IsToolCapable);
        AssertEx.False(fromBlank.IsReasoningCapable);
        AssertEx.False(fromBlank.IsNativeReasoningCapable);
    }
}

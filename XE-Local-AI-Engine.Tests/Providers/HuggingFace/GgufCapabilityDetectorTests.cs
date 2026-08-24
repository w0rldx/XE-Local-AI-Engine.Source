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

    /// <summary>
    ///     A Qwen3-style template that opens a thinking channel but NEVER renders a closing marker. It is graded
    ///     reasoning-capable — the <c>&lt;think</c> / <c>enable_thinking</c> / <c>reasoning_content</c> markers are all
    ///     there — yet llama.cpp's chat-template classification would find no think-END tag for it, so a per-request
    ///     <c>reasoning_budget_tokens</c> would be accepted and then silently ignored. This is the exact shape the
    ///     enforceability flag exists to separate from the two real families below.
    /// </summary>
    private const string Qwen3ReasoningTemplate =
        "{%- if enable_thinking %}<think>\\n{%- endif %}{%- if message.reasoning_content %}{{ message.reasoning_content }}{%- endif %}";

    /// <summary>
    ///     A verbatim excerpt of the REAL Qwen3.x reasoning shape, copied from the <c>tokenizer.chat_template</c> in the
    ///     GGUF header of <c>unsloth/qwen3.8-27b-GGUF:Q4_K_M</c> (read 2026-08-24, where <c>&lt;/think&gt;</c> occurs
    ///     2×). The reasoning text is rendered BETWEEN <c>&lt;think&gt;</c> and <c>&lt;/think&gt;</c>, which is exactly
    ///     the pattern llama.cpp's generic differential autoparser diffs out into a non-empty think-end-tag set — the
    ///     precondition for its <c>reasoning_budget_tokens</c> gate. Live-confirmed on this template family at b10201.
    /// </summary>
    private const string Qwen3ClosingTagReasoningTemplate =
        "{%- set reasoning_content = reasoning_content|trim %}"
        + "{{- '<|im_start|>' + message.role + '\\n<think>\\n' + reasoning_content + '\\n</think>\\n\\n' + content }}"
        + "{%- if enable_thinking is defined and enable_thinking is false %}{{- '<think>\\n\\n</think>\\n\\n' }}{%- endif %}";

    /// <summary>
    ///     A verbatim excerpt of the gemma-4 reasoning shape, copied from the <c>tokenizer.chat_template</c> in the GGUF
    ///     header of <c>unsloth/gemma-4-12b-it-GGUF:Q4_K_M</c> (read 2026-08-24, where <c>&lt;channel|&gt;</c> occurs
    ///     3×). gemma-4 uses no <c>&lt;think&gt;</c> pair at all: the thinking text is closed by <c>&lt;channel|&gt;</c>,
    ///     which llama.cpp's specialised gemma4 parser hardcodes as its think-end tag. The excerpt keeps the literal
    ///     <c>reasoning_content</c> too, because that — not a think marker — is what makes gemma-4 grade as reasoning.
    /// </summary>
    private const string Gemma4ReasoningTemplate =
        "{%- set thinking_text = message.get('reasoning') or message.get('reasoning_content') -%}"
        + "{%- if thinking_text and thinking_gate -%}{{- '<|channel>thought\\n' + thinking_text + '\\n<channel|>' -}}{%- endif -%}"
        + "{%- if not enable_thinking -%}{{- '<|channel>thought\\n<channel|>' -}}{%- endif -%}";

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
    ///     The harmony template reasons on its own channel, so the model MUST be reported reasoning-capable —
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

    /// <summary>
    ///     The Qwen3.x family: graded reasoning AND an enforceable budget. The template renders the reasoning between
    ///     <c>&lt;think&gt;</c> and <c>&lt;/think&gt;</c>, so llama.cpp's differential autoparser recovers a think-END
    ///     tag and its <c>reasoning_budget_tokens</c> gate passes — live-verified against this exact model at b10201
    ///     (255 reasoning tokens under a 256 budget vs 2 385 uncapped).
    /// </summary>
    [Test]
    public void Detect_Qwen3ClosingTagTemplate_IsReasoningCapableAndBudgetEnforceable()
    {
        var result = GgufCapabilityDetector.Detect(Qwen3ClosingTagReasoningTemplate);

        AssertEx.True(result.IsReasoningCapable, "the real Qwen3.x template exposes a graded thinking channel");
        AssertEx.True(result.ReasoningBudgetEnforceable,
            "a template that renders </think> after the reasoning gives llama.cpp the end tag its budget gate requires");
    }

    /// <summary>
    ///     The gemma-4 family: the SAME verdict reached through a completely different marker. gemma-4 carries no
    ///     <c>&lt;think&gt;</c> pair — its thinking channel closes with <c>&lt;channel|&gt;</c>, which llama.cpp's
    ///     specialised gemma4 parser hardcodes — so a detector that only knew the Qwen shape would wrongly drop a cap
    ///     that live testing proves holds (255 reasoning tokens under a 256 budget vs 2 382 uncapped, b10201).
    /// </summary>
    [Test]
    public void Detect_Gemma4Template_IsReasoningCapableAndBudgetEnforceable()
    {
        var result = GgufCapabilityDetector.Detect(Gemma4ReasoningTemplate);

        AssertEx.True(result.IsReasoningCapable, "gemma-4's template renders reasoning_content, so it grades as reasoning-capable");
        AssertEx.True(result.ReasoningBudgetEnforceable,
            "gemma-4 closes its thinking channel with <channel|>, the end tag its specialised llama.cpp parser hardcodes");
    }

    /// <summary>
    ///     The regression this flag exists for, and the test that previously graded this template as budget-enforceable
    ///     by omission. A template can open a thinking channel and never close it: it stays GRADED reasoning-capable —
    ///     the effort control is genuinely available — while llama.cpp finds no think-end tag and therefore accepts a
    ///     <c>reasoning_budget_tokens</c> only to ignore it. Reporting the cap as enforceable here is what would let a
    ///     turn advertise a thinking budget that never fires.
    /// </summary>
    [Test]
    public void Detect_ClosingTagLessReasoningTemplate_IsReasoningCapableButNotBudgetEnforceable()
    {
        var result = GgufCapabilityDetector.Detect(Qwen3ReasoningTemplate);

        AssertEx.True(result.IsReasoningCapable, "an unclosed thinking channel is still a graded thinking channel");
        AssertEx.False(result.ReasoningBudgetEnforceable,
            "with no reasoning end marker in the template llama.cpp cannot enforce a thinking budget, so the flag must say so");
    }

    /// <summary>
    ///     Enforceability is asked only of a graded template, because that is the only kind a budget is ever sent for.
    ///     Every other classification keeps the inert <see langword="true" /> default rather than a stray
    ///     <see langword="false" /> a future caller could read as "drop the cap".
    /// </summary>
    [Test]
    public void Detect_NonGradedTemplates_ReportTheInertEnforceableDefault()
    {
        AssertEx.True(GgufCapabilityDetector.Detect(PlainChatTemplate).ReasoningBudgetEnforceable,
            "a plain template never receives a budget, so the flag stays at its inert default");
        AssertEx.True(GgufCapabilityDetector.Detect(HarmonyNativeReasoningTemplate).ReasoningBudgetEnforceable,
            "a native-reasoning model is never routed into the graded branch, so the flag stays at its inert default");
        AssertEx.True(GgufCapabilityDetector.Detect(null).ReasoningBudgetEnforceable,
            "no template to inspect is not evidence that a budget would fail to apply");
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

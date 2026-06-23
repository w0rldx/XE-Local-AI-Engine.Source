namespace XE_Local_AI_Engine.Tests.Providers.HuggingFace;

using XE_Local_AI_Engine.Providers.HuggingFace;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="GgufCapabilityDetector" />: deterministically classifies a GGUF model's tool / reasoning surface from
///     its embedded Jinja chat template. A tool-templated model is tool-capable, a thinking-channel template is
///     reasoning-capable, and a plain template (or no template) is neither — the safe default that never offers tools a
///     model cannot honor.
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
    public void Detect_PlainTemplate_IsNeitherToolNorReasoningCapable()
    {
        var result = GgufCapabilityDetector.Detect(PlainChatTemplate);

        AssertEx.False(result.IsToolCapable, "a plain template offers no tools");
        AssertEx.False(result.IsReasoningCapable, "a plain template exposes no thinking channel");
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
        AssertEx.Equal(expected: 1, fromNull.Capabilities.Count);
        AssertEx.Contains(fromNull.Capabilities, static c => c == "completion");
        AssertEx.False(fromBlank.IsToolCapable);
        AssertEx.False(fromBlank.IsReasoningCapable);
    }
}

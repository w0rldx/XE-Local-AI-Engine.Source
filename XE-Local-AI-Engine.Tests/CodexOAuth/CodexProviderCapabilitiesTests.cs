namespace XE_Local_AI_Engine.Tests.CodexOAuth;

using XE_Local_AI_Engine.Providers.CodexOAuth;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
/// Asserts the Codex provider capability matrix after the tool-calling flip (de-risk plan
/// <c>Plans/2026-06-08-codex-tool-calling-derisk.md</c>): tool calling is enabled for ALL Codex ids (D1) while
/// parallel tool calls stay off (single-call first, D2). The factory's <c>Capabilities</c> property and the chat /
/// model-list gates all read <see cref="CodexProviderCapabilities.V0"/>, so this single matrix governs the behaviour.
/// </summary>
public sealed class CodexProviderCapabilitiesTests
{
    [Test]
    public void V0_EnablesToolCalling_AndKeepsParallelToolCallsOff()
    {
        var capabilities = CodexProviderCapabilities.V0;

        // D1: tool calling on for all Codex ids.
        AssertEx.True(capabilities.SupportsToolCalling, "Codex must advertise tool calling");

        // D2: single-call first — parallel tool calls stay off.
        AssertEx.False(capabilities.SupportsParallelToolCalls, "parallel tool calls must remain off (single-call first)");

        // Unchanged neighbours: streaming on; the rest stay off as before the flip.
        AssertEx.True(capabilities.SupportsStreaming, "streaming stays on");
        AssertEx.False(capabilities.SupportsStructuredOutput);
        AssertEx.False(capabilities.SupportsVision);
        AssertEx.False(capabilities.SupportsUsage);
        AssertEx.False(capabilities.SupportsServiceSideThreads);
    }
}

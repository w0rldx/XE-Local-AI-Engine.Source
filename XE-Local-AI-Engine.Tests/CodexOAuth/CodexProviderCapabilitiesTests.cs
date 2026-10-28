namespace XE_Local_AI_Engine.Tests.CodexOAuth;

using XE_Local_AI_Engine.Providers.CodexOAuth;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The Codex provider capability matrix: tool calling is on for ALL Codex ids while parallel tool calls stay off
///     (single-call first). The factory's <c>Capabilities</c> and the chat / model-list gates all read
///     <see cref="CodexProviderCapabilities.V0" />, so this one matrix governs them.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class CodexProviderCapabilitiesTests
{
    [Test]
    public void V0_EnablesToolCalling_AndKeepsParallelToolCallsOff()
    {
        var capabilities = CodexProviderCapabilities.V0;

        // Tool calling on for all Codex ids.
        AssertEx.True(capabilities.SupportsToolCalling, "Codex must advertise tool calling");

        // Single-call first — parallel tool calls stay off.
        AssertEx.False(capabilities.SupportsParallelToolCalls, "parallel tool calls must remain off (single-call first)");

        // Streaming and usage on; the rest stay off.
        AssertEx.True(capabilities.SupportsStreaming, "streaming stays on");
        AssertEx.False(capabilities.SupportsStructuredOutput);
        AssertEx.False(capabilities.SupportsVision);
        AssertEx.True(capabilities.SupportsUsage, "usage reaches the runner: CodexUsageReplayTests");
        AssertEx.False(capabilities.SupportsServiceSideThreads);
    }
}

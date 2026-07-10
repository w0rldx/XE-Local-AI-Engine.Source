namespace XE_Local_AI_Engine.Tests.Testing.Mocks;

using XE_Local_AI_Engine.AI.Agent.Instructions;

/// <summary>
///     Hand-written <see cref="IAgentInstructionProvider" /> fake. <see cref="IAgentInstructionProvider" /> is
///     internal to <c>XE-Local-AI-Engine.AI.Agent</c>, and Castle DynamicProxy (NSubstitute's proxy generator) cannot
///     build a proxy for an internal type without an assembly-level <c>InternalsVisibleTo("DynamicProxyGenAssembly2")</c>
///     grant — which this repo deliberately does not add. A hand-written fake sidesteps that entirely (mirrors
///     <c>FakeAgentToolRegistry</c> / <c>EmptyClientLocalToolRegistry</c> / <c>EmptyMcpToolRegistry</c>, the same
///     pattern used for every other internal AI.Agent interface in tests).
///     <see cref="BaseScaffold" /> defaults to empty, which <c>BaseInstructionComposer</c> treats as "no scaffold" —
///     so a test that does not care about scaffold composition keeps asserting the bare persona prompt unchanged.
/// </summary>
internal sealed class FakeAgentInstructionProvider : IAgentInstructionProvider
{
    public string LocalChatInstructions { get; set; } = string.Empty;

    public string BaseScaffold { get; set; } = string.Empty;

    public int ScaffoldVersion { get; set; } = 1;

    public string GetLocalChatInstructions()
    {
        return LocalChatInstructions;
    }

    public string GetBaseScaffold()
    {
        return BaseScaffold;
    }

    public string GetDefaultChatSystemPrompt()
    {
        return string.IsNullOrWhiteSpace(BaseScaffold) ? LocalChatInstructions : $"{BaseScaffold.TrimEnd()}\n\n{LocalChatInstructions}";
    }
}

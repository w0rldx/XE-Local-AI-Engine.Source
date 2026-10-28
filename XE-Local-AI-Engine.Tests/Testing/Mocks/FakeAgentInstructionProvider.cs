namespace XE_Local_AI_Engine.Tests.Testing.Mocks;

using XE_Local_AI_Engine.AI.Agent.Instructions;

/// <summary>
///     Hand-written <see cref="IAgentInstructionProvider" /> fake. <see cref="BaseScaffold" /> defaults to empty, which
///     <c>BaseInstructionComposer</c> treats as "no scaffold", so a test indifferent to scaffold composition still
///     asserts the bare persona prompt.
/// </summary>
/// <remarks>
///     The interface is internal to <c>XE-Local-AI-Engine.AI.Agent</c>, and Castle DynamicProxy (NSubstitute's proxy
///     generator) cannot proxy an internal type without an <c>InternalsVisibleTo("DynamicProxyGenAssembly2")</c> grant,
///     which this repo deliberately does not add; every internal AI.Agent interface uses this hand-written pattern.
/// </remarks>
internal sealed class FakeAgentInstructionProvider : IAgentInstructionProvider
{
    public string LocalChatInstructions { get; set; } = string.Empty;

    public string BaseScaffold { get; set; } = string.Empty;

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

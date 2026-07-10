namespace XE_Local_AI_Engine.AI.Agent.Tests.Instructions;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Instructions.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class AgentInstructionProviderTests
{
    private static AgentInstructionProvider CreateProvider()
    {
        return new AgentInstructionProvider(Options.Create(new LocalChatAgentOptions()));
    }

    [Test]
    public void GetLocalChatInstructions_ReadsTheConfiguredEmbeddedResource()
    {
        var provider = CreateProvider();

        var instructions = provider.GetLocalChatInstructions();

        AssertEx.True(!string.IsNullOrWhiteSpace(instructions), "the embedded local-chat instructions resource must not be blank.");
    }

    [Test]
    public void GetBaseScaffold_ReturnsNonBlankVersionedScaffold()
    {
        var provider = CreateProvider();

        var scaffold = provider.GetBaseScaffold();

        AssertEx.True(!string.IsNullOrWhiteSpace(scaffold), "the embedded base scaffold resource must not be blank.");
        AssertEx.True(provider.ScaffoldVersion >= 1, "the scaffold version must be a positive, incrementable counter.");
    }

    [Test]
    public void GetDefaultChatSystemPrompt_ComposesScaffoldAheadOfLocalChatInstructions()
    {
        var provider = CreateProvider();

        var scaffold = provider.GetBaseScaffold();
        var persona = provider.GetLocalChatInstructions();
        var composed = provider.GetDefaultChatSystemPrompt();

        AssertEx.Equal($"{scaffold.TrimEnd()}\n\n{persona}", composed);
    }
}

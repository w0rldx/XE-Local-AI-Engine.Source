#if DEBUG
namespace XE_Local_AI_Engine.AI.Agent.Tests.DependencyInjection;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.DependencyInjection;
using XE_Local_AI_Engine.AI.Agent.Instructions;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class AgentDevUiExtensionsTests
{
    [Test]
    public void AddLocalAiAgentDevUi_DebugRegistration_ResolvesTheNamedRepresentativeAgent()
    {
        const string instructions = "DEVUI_INSTRUCTIONS_MARKER";
        var builder = Host.CreateApplicationBuilder();
        var chatClient = Substitute.For<IChatClient>();
        var instructionProvider = Substitute.For<IAgentInstructionProvider>();
        instructionProvider.GetLocalChatInstructions().Returns(instructions);
        builder.Services.AddSingleton(chatClient);
        builder.Services.AddSingleton(instructionProvider);

        _ = builder.AddLocalAiAgentDevUi();

        using var host = builder.Build();
        var agent = host.Services.GetRequiredKeyedService<AIAgent>("xe-local-ai");
        var chatClientAgent = AssertEx.NotNull(agent as ChatClientAgent);
        AssertEx.Equal("xe-local-ai", chatClientAgent.Name, "the DevUI registration key must remain the agent identity");
        AssertEx.Equal(instructions, chatClientAgent.Instructions, "the representative agent must use the app-owned local chat instructions");
    }
}
#endif

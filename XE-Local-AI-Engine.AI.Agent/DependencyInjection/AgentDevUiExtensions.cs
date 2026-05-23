namespace XE_Local_AI_Engine.AI.Agent.DependencyInjection;

using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.AI.Agent.Instructions;

/// <summary>
/// Development-only registration of a representative named agent so the Microsoft Agent
/// Framework DevUI dashboard has an agent to list and chat with.
/// </summary>
/// <remarks>
/// Worker invocations build agents per-request via <see cref="Invocation.IInvocationAgentFactory"/>,
/// which DevUI cannot enumerate. This registers a single long-lived agent that reuses the same
/// decorated <see cref="IChatClient"/> (tool-invocation + function-invocation pipeline) and the
/// same local-chat instructions, giving the DevUI playground a faithful view of the agent stack.
/// Endpoint mapping (MapDevUI / MapOpenAIResponses) lives in the web host. Caller must guard with
/// <c>IsDevelopment()</c>.
/// </remarks>
public static class AgentDevUiExtensions
{
    public static IHostApplicationBuilder AddLocalAiAgentDevUi(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddAIAgent("xe-local-ai", static (serviceProvider, agentName) =>
        {
            var chatClient = serviceProvider.GetRequiredService<IChatClient>();
            var instructions = serviceProvider.GetRequiredService<IAgentInstructionProvider>().GetLocalChatInstructions();
            var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
            IList<AITool> tools = [];

            // ChatClientAgent ctor order is (chatClient, instructions, name, description, ...).
            // The agent's Name must equal the registration key, so use the factory-supplied name.
            return new ChatClientAgent(
                chatClient,
                instructions,
                agentName,
                "XE Local AI Engine DevUI playground agent.",
                tools,
                loggerFactory,
                serviceProvider);
        });

        return builder;
    }
}

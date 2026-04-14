namespace XE_Local_AI_Engine.AI.Agent.Tools;

using Microsoft.Extensions.AI;

internal sealed class LocalAgentToolRegistry : IAgentToolRegistry
{
    public IReadOnlyList<AITool> GetLocalChatTools()
    {
        return [];
    }
}

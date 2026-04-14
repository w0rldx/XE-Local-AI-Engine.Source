namespace XE_Local_AI_Engine.AI.Agent.Tools;

using Microsoft.Extensions.AI;

internal interface IAgentToolRegistry
{
    IReadOnlyList<AITool> GetLocalChatTools();
}

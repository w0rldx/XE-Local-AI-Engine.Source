namespace XE_Local_AI_Engine.Client.Services.Mcp;

using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.AI.Agent.Tools;

public interface IMcpAgenticToolAdapter
{
    AIFunction Adapt(ApprovalRequiredAIFunction approvalRequired,
        ToolCategory category,
        McpInboundExecutionContext context,
        Guid requestId);
}

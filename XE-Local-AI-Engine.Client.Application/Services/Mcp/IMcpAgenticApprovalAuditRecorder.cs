namespace XE_Local_AI_Engine.Client.Services.Mcp;

using XE_Local_AI_Engine.AI.Agent.Tools;

/// <summary>Strict, fail-closed audit writer for agentic MCP tool invocations.</summary>
public interface IMcpAgenticApprovalAuditRecorder
{
    Task RecordAsync(Guid requestId,
        string toolName,
        ToolCategory category,
        string keyPrefix,
        CancellationToken cancellationToken = default);
}

namespace XE_Local_AI_Engine.Client.Services.Mcp.Runs;

using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Capacity;

/// <summary>
///     Executes one claimed durable run. Implementations return only after every execution resource acquired by the
///     request has been released.
/// </summary>
public interface IMcpAgentRunExecutor
{
    Task<SpawnOutcome> ExecuteAsync(McpAgentRunRecord run, CancellationToken cancellationToken);
}

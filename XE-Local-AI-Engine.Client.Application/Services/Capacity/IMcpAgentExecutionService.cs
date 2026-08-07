namespace XE_Local_AI_Engine.Client.Services.Capacity;

/// <summary>
///     Executes an external, unattended MCP delegation through the inbound-only binding and capability policy. This is
///     intentionally separate from <see cref="ISubAgentSpawnService" />, whose contract belongs to trusted in-process
///     agent orchestration.
/// </summary>
public interface IMcpAgentExecutionService
{
    /// <summary>
    ///     Resolves and runs an inbound request. When <paramref name="expectedBindingFingerprint" /> is supplied,
    ///     execution is rejected if the repeatable binding no longer matches the caller's accepted snapshot.
    /// </summary>
    Task<SpawnOutcome> SpawnForMcpAsync(McpExecutionBindingRequest request,
        string task,
        string? expectedBindingFingerprint,
        CancellationToken ct,
        Guid? workspaceId = null);
}

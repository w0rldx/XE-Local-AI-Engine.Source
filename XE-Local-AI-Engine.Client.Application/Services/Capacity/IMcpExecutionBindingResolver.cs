namespace XE_Local_AI_Engine.Client.Services.Capacity;

/// <summary>
///     Resolves the complete inbound MCP execution configuration. Calling this method again at claim time and comparing
///     <see cref="McpExecutionBinding.BindingFingerprint" /> detects definition, model, prompt, reasoning, and tool drift.
/// </summary>
public interface IMcpExecutionBindingResolver
{
    Task<McpExecutionBindingResolution> ResolveAsync(McpExecutionBindingRequest request, CancellationToken cancellationToken);
}

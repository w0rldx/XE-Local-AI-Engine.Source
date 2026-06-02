namespace XE_Local_AI_Engine.Client.Services.HostAgent;

/// <summary>
///     Client boundary for HostAgent readiness checks used before model-dependent startup work.
/// </summary>
public interface IHostAgentReadinessClient
{
    Task<bool> IsBootstrapModelReadyAsync(CancellationToken cancellationToken);
}

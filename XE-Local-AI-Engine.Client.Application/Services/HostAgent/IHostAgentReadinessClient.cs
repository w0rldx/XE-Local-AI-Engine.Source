namespace XE_Local_AI_Engine.Client.Services.HostAgent;

/// <summary>
///     Client boundary for i host agent readiness operations.
/// </summary>
public interface IHostAgentReadinessClient
{
    Task<bool> IsBootstrapModelReadyAsync(CancellationToken cancellationToken);
}

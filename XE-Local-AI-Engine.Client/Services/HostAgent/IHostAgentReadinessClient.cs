namespace XE_Local_AI_Engine.Client.Services.HostAgent;

public interface IHostAgentReadinessClient
{
    Task<bool> IsBootstrapModelReadyAsync(CancellationToken cancellationToken);
}

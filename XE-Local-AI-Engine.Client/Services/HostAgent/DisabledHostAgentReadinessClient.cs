namespace XE_Local_AI_Engine.Client.Services.HostAgent;

public sealed class DisabledHostAgentReadinessClient : IHostAgentReadinessClient
{
    public Task<bool> IsBootstrapModelReadyAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(true);
    }
}

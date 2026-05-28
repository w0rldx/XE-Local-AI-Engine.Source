namespace XE_Local_AI_Engine.Client.Services.HostAgent.Implementation;

public sealed class DisabledHostAgentReadinessClient : IHostAgentReadinessClient
{
    public Task<bool> IsBootstrapModelReadyAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(true);
    }
}

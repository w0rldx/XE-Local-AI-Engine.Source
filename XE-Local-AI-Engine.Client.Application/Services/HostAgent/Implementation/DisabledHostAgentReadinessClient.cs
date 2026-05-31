namespace XE_Local_AI_Engine.Client.Services.HostAgent.Implementation;

/// <summary>
///     Client boundary for disabled host agent readiness operations.
/// </summary>
public sealed class DisabledHostAgentReadinessClient : IHostAgentReadinessClient
{
    public Task<bool> IsBootstrapModelReadyAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(true);
    }
}

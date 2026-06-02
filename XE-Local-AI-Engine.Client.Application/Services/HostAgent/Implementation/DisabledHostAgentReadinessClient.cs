namespace XE_Local_AI_Engine.Client.Services.HostAgent.Implementation;

/// <summary>
///     Readiness client used when HostAgent startup gating is disabled.
/// </summary>
public sealed class DisabledHostAgentReadinessClient : IHostAgentReadinessClient
{
    public Task<bool> IsBootstrapModelReadyAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(true);
    }
}

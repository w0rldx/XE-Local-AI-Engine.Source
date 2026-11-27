namespace XE_Local_AI_Engine.Tests.Testing;

using XE_Local_AI_Engine.Client.Services.AgentHome;

/// <summary>Test double for <see cref="IAgentHomeIdentityProvider" />: every call returns the owner and node it was built with.</summary>
internal sealed class FixedAgentHomeIdentityProvider : IAgentHomeIdentityProvider
{
    private readonly string _ownerUserId;
    private readonly string _nodeId;

    public FixedAgentHomeIdentityProvider(string ownerUserId, string nodeId)
    {
        _ownerUserId = ownerUserId;
        _nodeId = nodeId;
    }

    public Task<AgentHomeOwnerIdentity> GetAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new AgentHomeOwnerIdentity
        {
            OwnerUserId = _ownerUserId,
            NodeId = _nodeId
        });
}

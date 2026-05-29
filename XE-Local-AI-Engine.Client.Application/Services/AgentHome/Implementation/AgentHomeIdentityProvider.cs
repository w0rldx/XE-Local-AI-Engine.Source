namespace XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;

using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     MVP <see cref="IAgentHomeIdentityProvider" />. Sources the node id from <see cref="ITokenStore" />, falling
///     back to the deterministic local-loopback node id when the worker is not yet paired. The node is single-user in
///     the MVP, so the owner boundary is the node itself; a true per-user owner (decoded from the access-token
///     subject) is deferred to Marker I, when the distributed/multi-user path makes owner changes reachable.
/// </summary>
internal sealed class AgentHomeIdentityProvider : IAgentHomeIdentityProvider
{
    private readonly ITokenStore _tokenStore;

    public AgentHomeIdentityProvider(ITokenStore tokenStore)
    {
        _tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
    }

    public async Task<AgentHomeOwnerIdentity> GetAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var clientNodeId = await _tokenStore.GetClientNodeIdAsync().ConfigureAwait(false)
                           ?? LocalChatLoopbackDefaults.ClientNodeId;
        var nodeId = clientNodeId.ToString();

        // MVP owner boundary = the node itself (single-user node). Marker I derives the real owner subject from the
        // access token when the distributed/multi-user envelope path goes live.
        return new AgentHomeOwnerIdentity(nodeId, nodeId);
    }
}

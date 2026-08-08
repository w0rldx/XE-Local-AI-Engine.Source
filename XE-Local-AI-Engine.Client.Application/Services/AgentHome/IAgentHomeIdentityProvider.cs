namespace XE_Local_AI_Engine.Client.Services.AgentHome;

/// <summary>
///     Supplies the owner/node identity used to build a <c>SandboxAttachKey</c> for AgentHome runs. The
///     <c>run_in_agent_home</c> handler is JSON-in / JSON-out and carries no owner context, and the worker persists
///     only its node id (the owner is a token subject claim), so the orchestration sources identity through this seam.
///     The current implementation is node-scoped; future owner refinement belongs at the AgentHome gateway boundary.
/// </summary>
internal interface IAgentHomeIdentityProvider
{
    /// <summary>Resolves the current owner/node identity for the node-scoped AgentHome sandbox.</summary>
    Task<AgentHomeOwnerIdentity> GetAsync(CancellationToken cancellationToken = default);
}

/// <summary>The owner/node pair that scopes a node's AgentHome sandbox.</summary>
internal sealed record AgentHomeOwnerIdentity(string OwnerUserId, string NodeId);

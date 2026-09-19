namespace XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     Read boundary for the worker credentials an earlier build could write to the node data directory.
///     Nothing writes them any more — the Central Platform pairing flow they came from is gone — so on every node
///     that never paired both members return null and the caller falls back to the deterministic local-loopback
///     identity (see <c>AgentHomeIdentityProvider</c>). A node that DID pair keeps returning the node id it was
///     issued, so its AgentHome identity does not change under it.
/// </summary>
public interface ITokenStore
{
    /// <summary>The stored access token, or null when none is stored or the stored one has expired.</summary>
    Task<string?> GetAccessTokenAsync();

    /// <summary>The stored node id, or null when no credentials are stored.</summary>
    Task<Guid?> GetClientNodeIdAsync();
}

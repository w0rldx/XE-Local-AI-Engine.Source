namespace XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     Represents node auth rate limits.
/// </summary>
public static class NodeAuthRateLimits
{
    public const string AuthPolicy = "AuthRateLimit";

    /// <summary>
    ///     Guards the inbound MCP endpoint against offline-speed guessing of the bearer key. Separate from
    ///     <see cref="AuthPolicy" /> and deliberately far more permissive: an authenticated MCP client makes many
    ///     legitimate calls per minute (tools/list on connect, then a call per delegated task), so the auth endpoints'
    ///     10/min would break normal use. The cap exists to bound BRUTE FORCE, not to shape traffic.
    /// </summary>
    public const string McpPolicy = "McpRateLimit";

    /// <summary>
    ///     Bounds a runaway or misbehaving client on the inbound model-proxy endpoints. It is deliberately far more
    ///     permissive than every other policy here and is NOT a key-guessing defense: the 256-bit key is uncrackable
    ///     regardless of the cap, and a single authenticated client doing RAG/document indexing legitimately issues
    ///     thousands of embedding calls per minute — so shaping that traffic would break normal use. Real per-model
    ///     compute is already bounded by the loaded-model cap and inference leases; this only stops one local process
    ///     from hammering the node unbounded.
    /// </summary>
    public const string LocalModelProxyPolicy = "LocalModelProxyRateLimit";
}

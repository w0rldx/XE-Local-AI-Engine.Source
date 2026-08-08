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
}

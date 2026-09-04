namespace XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     Represents node authorization policies.
/// </summary>
public static class NodeAuthorizationPolicies
{
    public const string Operator = "NodeOperator";

    /// <summary>
    ///     Gates the inbound MCP server endpoint. Deliberately SEPARATE from <see cref="Operator" /> and satisfied by a
    ///     different authentication scheme (the MCP API key, not JWT): an external MCP client is a strictly lesser
    ///     principal than the node operator and must never inherit the operator's reach over the local admin API.
    /// </summary>
    public const string McpServer = "McpServer";

    /// <summary>Gates MCP tools that may change node configuration without browser-operator involvement.</summary>
    public const string McpAgentic = "McpAgentic";

    public const string McpScopeClaimType = "xe:mcp_scope";
    public const string McpKeyPrefixClaimType = "xe:mcp_key_prefix";
    public const string McpAgenticScope = "agentic";
    public const string McpDelegateScope = "delegate";

    /// <summary>
    ///     Gates the inbound OpenAI-compatible model proxy. Like <see cref="McpServer" /> it is SEPARATE from
    ///     <see cref="Operator" /> and satisfied by its own authentication scheme (the model-proxy API key, not JWT):
    ///     an external tool that only consumes the raw model is a strictly lesser principal than the node operator and
    ///     must never inherit the operator's reach over the local admin API — nor the MCP client's reach over the agent
    ///     tool surface.
    /// </summary>
    public const string LocalModelProxy = "LocalModelProxy";

    /// <summary>
    ///     Gates the hand-mapped external integration API. Like <see cref="McpServer" /> and
    ///     <see cref="LocalModelProxy" /> it is SEPARATE from <see cref="Operator" /> and satisfied by its own
    ///     authentication scheme (an <c>xeint_</c> key, never JWT): an external integrator is a strictly lesser
    ///     principal than the node operator and must never inherit the operator's reach over the local admin API — nor
    ///     the MCP client's reach over the agent tool surface.
    /// </summary>
    public const string IntegrationApi = "IntegrationApi";

    /// <summary>
    ///     The integrator identity. <b>This is the claim that carries authority</b>: session and execution ownership,
    ///     the request-uniqueness index, the dedup fingerprint and the masking rule all key on it, so two keys issued
    ///     to the same integrator share everything a rotation must not strand.
    /// </summary>
    public const string IntegrationPrincipalClaimType = "xe:integration_principal";

    /// <summary>
    ///     Which credential made the call. <b>Attribution only</b> — nothing reads it to decide anything, and a
    ///     decision written against it would break the moment a key is rotated.
    /// </summary>
    public const string IntegrationKeyPrefixClaimType = "xe:integration_key_prefix";

    public const string AdminRole = "Admin";
    public const string RoleClaimType = "role";

    /// <summary>
    ///     Carries the user's ASP.NET Identity security stamp inside the access token. Every password mutation
    ///     (reset, change) rotates the stamp, so JWT bearer validation can reject a still-unexpired access token minted
    ///     before the change — giving otherwise-stateless JWTs a real per-user invalidation point instead of waiting out
    ///     the token lifetime. Kept short because it rides in every request's token.
    /// </summary>
    public const string SecurityStampClaimType = "xe:sst";
}

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

    public const string AdminRole = "Admin";
    public const string RoleClaimType = "role";
}

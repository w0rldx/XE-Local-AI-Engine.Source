namespace XE_Local_AI_Engine.Client.Services.Mcp;

using System.Security.Claims;
using XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>Trusted, explicit authority captured from the authenticated inbound MCP principal.</summary>
public sealed record McpInboundExecutionContext(McpServerApiKeyScope Scope, string? KeyPrefix)
{
    public static McpInboundExecutionContext Delegate { get; } = new(McpServerApiKeyScope.Delegate, KeyPrefix: null);

    public bool IsAgentic => Scope == McpServerApiKeyScope.Agentic;

    public static McpInboundExecutionContext FromPrincipal(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return Delegate;
        }

        var scopes = principal.FindAll(NodeAuthorizationPolicies.McpScopeClaimType).Select(static claim => claim.Value).ToArray();
        if (scopes.Length != 1 || !string.Equals(scopes[0], NodeAuthorizationPolicies.McpAgenticScope, StringComparison.Ordinal))
        {
            return Delegate;
        }

        var prefixes = principal.FindAll(NodeAuthorizationPolicies.McpKeyPrefixClaimType).Select(static claim => claim.Value).ToArray();
        return prefixes.Length == 1 && IsBoundedPrefix(prefixes[0])
            ? new McpInboundExecutionContext(McpServerApiKeyScope.Agentic, prefixes[0])
            : Delegate;
    }

    public static bool IsBoundedPrefix(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 32
        && value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
}

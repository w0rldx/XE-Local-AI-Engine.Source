namespace XE_Local_AI_Engine.Tests.Mcp;

using System.Security.Claims;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Mcp;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class McpInboundExecutionContextTests
{
    [Test]
    public void FromPrincipal_WhenTrustedClaimsAreExact_CapturesAgenticAuthority()
    {
        var principal = Principal(
            new Claim(NodeAuthorizationPolicies.McpScopeClaimType, NodeAuthorizationPolicies.McpAgenticScope),
            new Claim(NodeAuthorizationPolicies.McpKeyPrefixClaimType, "xemcp_abc123"));

        var context = McpInboundExecutionContext.FromPrincipal(principal);

        AssertEx.True(context.IsAgentic);
        AssertEx.Equal("xemcp_abc123", context.KeyPrefix!);
    }

    [Test]
    public void FromPrincipal_WhenClaimsAreMissingDuplicatedOrUnbounded_FailsClosedToDelegate()
    {
        var principals = new[]
        {
            Principal(new Claim(NodeAuthorizationPolicies.McpScopeClaimType, NodeAuthorizationPolicies.McpAgenticScope)),
            Principal(new Claim(NodeAuthorizationPolicies.McpScopeClaimType, NodeAuthorizationPolicies.McpAgenticScope),
                new Claim(NodeAuthorizationPolicies.McpScopeClaimType, NodeAuthorizationPolicies.McpAgenticScope),
                new Claim(NodeAuthorizationPolicies.McpKeyPrefixClaimType, "xemcp_abc123")),
            Principal(new Claim(NodeAuthorizationPolicies.McpScopeClaimType, NodeAuthorizationPolicies.McpAgenticScope),
                new Claim(NodeAuthorizationPolicies.McpKeyPrefixClaimType, new string('a', 33)))
        };

        AssertEx.True(principals.All(static principal => !McpInboundExecutionContext.FromPrincipal(principal).IsAgentic));
    }

    private static ClaimsPrincipal Principal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, "McpApiKey"));
}

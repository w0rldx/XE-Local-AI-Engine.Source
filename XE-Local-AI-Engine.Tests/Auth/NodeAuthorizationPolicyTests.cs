namespace XE_Local_AI_Engine.Tests.Auth;

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Pins the shape of the node's three authorization policies as the container actually resolves them, not as the
///     registration source reads. The separation is the whole point: <see cref="NodeAuthorizationPolicies.McpServer" />
///     and <see cref="NodeAuthorizationPolicies.LocalModelProxy" /> each accept exactly ONE non-JWT scheme, so a browser
///     session or a stolen operator token can never drive them, and the operator policy is the only one that carries a
///     role requirement. A silent loosening — an extra scheme added to a policy, or the admin role dropped — compiles
///     and passes every other suite, so it is asserted here directly.
/// </summary>
public sealed class NodeAuthorizationPolicyTests
{
    [ClassDataSource<TestServerWebAppFactory>(Shared = SharedType.PerClass)]
    public required TestServerWebAppFactory Factory { get; init; }

    [Test]
    public async Task OperatorPolicy_RequiresAnAuthenticatedAdminOnTheJwtSchemeOnly()
    {
        var policy = await GetPolicyAsync(NodeAuthorizationPolicies.Operator);

        AssertEx.Equal(expected: 1, policy.AuthenticationSchemes.Count);
        AssertEx.Equal(JwtBearerDefaults.AuthenticationScheme, policy.AuthenticationSchemes[0]);
        AssertEx.ContainsSingle(policy.Requirements, requirement => requirement is DenyAnonymousAuthorizationRequirement);
        var roles = AssertEx.NotNull(policy.Requirements.OfType<RolesAuthorizationRequirement>().SingleOrDefault());
        AssertEx.Equal(expected: 1, roles.AllowedRoles.Count());
        AssertEx.Equal(NodeAuthorizationPolicies.AdminRole, roles.AllowedRoles.Single());
    }

    [Test]
    [Arguments(NodeAuthorizationPolicies.McpServer, "McpApiKey")]
    [Arguments(NodeAuthorizationPolicies.LocalModelProxy, "LocalModelProxyApiKey")]
    public async Task ApiKeyPolicy_AcceptsOnlyItsOwnSchemeAndCarriesNoRoleRequirement(string policyName, string expectedScheme)
    {
        var policy = await GetPolicyAsync(policyName);

        AssertEx.Equal(expected: 1, policy.AuthenticationSchemes.Count);
        AssertEx.Equal(expectedScheme, policy.AuthenticationSchemes[0]);
        AssertEx.False(policy.AuthenticationSchemes.Contains(JwtBearerDefaults.AuthenticationScheme, StringComparer.Ordinal),
            "The API-key policies must never inherit the operator's JWT scheme.");
        AssertEx.ContainsSingle(policy.Requirements, requirement => requirement is DenyAnonymousAuthorizationRequirement);
        AssertEx.Empty(policy.Requirements.OfType<RolesAuthorizationRequirement>());
    }

    [Test]
    public async Task DefaultPolicy_StillOnlyRequiresAnAuthenticatedUser()
    {
        // The default policy is what an endpoint gets from a bare [Authorize]; widening it would silently grant reach.
        var provider = Factory.Services.GetRequiredService<IAuthorizationPolicyProvider>();
        var policy = await provider.GetDefaultPolicyAsync();

        AssertEx.ContainsSingle(policy.Requirements, requirement => requirement is DenyAnonymousAuthorizationRequirement);
        AssertEx.Empty(policy.Requirements.OfType<RolesAuthorizationRequirement>());
    }

    [Test]
    public async Task OperatorEndpoint_WithoutAToken_Returns401()
    {
        using var client = Factory.CreateClient();

        using var response = await client.GetAsync(OperatorEndpoint);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task OperatorEndpoint_WithAnAuthenticatedNonAdminToken_Returns403()
    {
        using var client = Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, OperatorEndpoint);
        request.Headers.Authorization =
            new AuthenticationHeaderValue(JwtBearerDefaults.AuthenticationScheme, Factory.CreateNonOperatorAccessToken("Viewer"));

        using var response = await client.SendAsync(request);

        // Authenticated but unauthorized: the role requirement, not the authentication, is what rejects this.
        AssertEx.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Test]
    public async Task OperatorEndpoint_WithTheAdminToken_IsNeitherUnauthorizedNorForbidden()
    {
        using var client = Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, OperatorEndpoint);
        Factory.AddNodeBearerToken(request);

        using var response = await client.SendAsync(request);

        AssertEx.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        AssertEx.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Test]
    public async Task ModelProxyEndpoint_RejectsBothAnonymousAndTheOperatorToken()
    {
        using var client = Factory.CreateClient();
        var path = "/" + LocalApiRoutes.Prefix + "/" + LocalApiRoutes.Proxy.Models;

        using var anonymous = await client.GetAsync(path);
        AssertEx.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        Factory.AddNodeBearerToken(request);
        using var withOperatorToken = await client.SendAsync(request);

        // The operator's JWT is not this policy's scheme, so it authenticates nothing here: still a challenge, never a
        // 200 and never a 403 (a 403 would mean the token had been accepted as a principal).
        AssertEx.Equal(HttpStatusCode.Unauthorized, withOperatorToken.StatusCode);
    }

    [Test]
    public async Task McpServerEndpoint_RejectsBothAnonymousAndTheOperatorToken()
    {
        using var client = Factory.CreateClient();
        var path = "/" + LocalApiRoutes.Prefix + "/" + LocalApiRoutes.Mcp.ServerEndpoint;

        using var anonymousContent = JsonRpcPing();
        using var anonymous = await client.PostAsync(path, anonymousContent);
        AssertEx.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonRpcPing()
        };
        Factory.AddNodeBearerToken(request);
        using var withOperatorToken = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.Unauthorized, withOperatorToken.StatusCode);
    }

    private static StringContent JsonRpcPing() =>
        new("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", Encoding.UTF8, "application/json");

    private static string OperatorEndpoint => "/" + LocalApiRoutes.Prefix + "/" + LocalApiRoutes.LocalModels.Models;

    private async Task<AuthorizationPolicy> GetPolicyAsync(string policyName)
    {
        var provider = Factory.Services.GetRequiredService<IAuthorizationPolicyProvider>();
        return AssertEx.NotNull(await provider.GetPolicyAsync(policyName), $"Policy '{policyName}' is not registered.");
    }
}

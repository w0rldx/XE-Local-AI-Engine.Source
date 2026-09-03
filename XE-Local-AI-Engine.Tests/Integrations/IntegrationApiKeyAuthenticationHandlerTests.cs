namespace XE_Local_AI_Engine.Tests.Integrations;

using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Integrations;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The <c>IntegrationApiKey</c> scheme. Two properties carry the security argument: a REVOKED key is
///     indistinguishable from an unknown one (ruling R2-6 — no 403 exists anywhere on this family), and the identity it
///     mints carries the integrator's principal as the authoritative claim with the credential prefix as attribution
///     only (ruling R4-6).
/// </summary>
public sealed class IntegrationApiKeyAuthenticationHandlerTests
{
    private const string ValidKey = "xeint_abcdefghijklmnopqrstuvwxyz0123456789";

    private const string RotatedKey = "xeint_zyxwvutsrqponmlkjihgfedcba9876543210";

    [Test]
    public async Task Authenticate_WithNoAuthorizationHeader_ProducesNoResult()
    {
        // NoResult rather than Fail, so the challenge that emits WWW-Authenticate still runs.
        await using var factory = CreateFactory();

        var result = await AuthenticateAsync(factory, presented: null).ConfigureAwait(false);

        AssertEx.False(result.Succeeded);
        AssertEx.True(result.None, "A missing credential is not an authentication failure.");
    }

    [Test]
    [Arguments("Basic dXNlcjpwYXNz")]
    [Arguments("xeint_no-bearer-prefix")]
    public async Task Authenticate_WithANonBearerScheme_Fails(string header)
    {
        await using var factory = CreateFactory();
        await using var scope = factory.Services.CreateAsyncScope();
        var context = CreateContext(scope);
        context.Request.Headers.Authorization = header;

        var result = await context.AuthenticateAsync(IntegrationApiKeyAuthenticationHandler.SchemeName).ConfigureAwait(false);

        AssertEx.False(result.Succeeded);
    }

    [Test]
    [Arguments("xeint_wrong-key-material-goes-right-here")]
    [Arguments("x")]
    [Arguments("Bearer x")]
    [Arguments("")]
    public async Task Authenticate_WithAnInvalidOrShortKey_FailsWithoutThrowing(string presented)
    {
        // A short value must not reach an unguarded slice inside the key service: the handler has no try/catch, so an
        // exception there is a 500 where a 401 is required, reachable by anyone.
        await using var factory = CreateFactory();

        var result = await AuthenticateAsync(factory, presented).ConfigureAwait(false);

        AssertEx.False(result.Succeeded);
    }

    [Test]
    public async Task Authenticate_WithAValidKey_MintsPrincipalAndPrefixClaimsAndNoRole()
    {
        await using var factory = CreateFactory();

        var result = await AuthenticateAsync(factory, ValidKey).ConfigureAwait(false);

        AssertEx.True(result.Succeeded, "A live credential must authenticate.");
        var principal = AssertEx.NotNull(result.Principal);
        AssertEx.Equal(PrincipalId.ToString("D"), principal.FindFirst(NodeAuthorizationPolicies.IntegrationPrincipalClaimType)?.Value);
        AssertEx.Equal("xeint_aaaaaaaa", principal.FindFirst(NodeAuthorizationPolicies.IntegrationKeyPrefixClaimType)?.Value);
        AssertEx.False(principal.IsInRole(NodeAuthorizationPolicies.AdminRole),
            "An integrator must never inherit the browser Operator role — the scheme carries no role claim at all.");
    }

    [Test]
    public async Task Authenticate_WithTwoKeysOfOneIntegrator_YieldsOnePrincipalAndTwoPrefixes()
    {
        // The rotation property ruling R4-6 exists for: ownership keys on the principal, so a second credential reaches
        // the same sessions and executions while remaining separately attributable and separately revocable.
        await using var factory = CreateFactory();

        var original = await AuthenticateAsync(factory, ValidKey).ConfigureAwait(false);
        var rotated = await AuthenticateAsync(factory, RotatedKey).ConfigureAwait(false);

        AssertEx.True(rotated.Succeeded);
        AssertEx.Equal(PrincipalId.ToString("D"), AssertEx.NotNull(original.Principal).FindFirst(NodeAuthorizationPolicies.IntegrationPrincipalClaimType)?.Value);
        AssertEx.Equal(PrincipalId.ToString("D"), AssertEx.NotNull(rotated.Principal).FindFirst(NodeAuthorizationPolicies.IntegrationPrincipalClaimType)?.Value);
        AssertEx.Equal("xeint_aaaaaaaa", AssertEx.NotNull(original.Principal).FindFirst(NodeAuthorizationPolicies.IntegrationKeyPrefixClaimType)?.Value);
        AssertEx.Equal("xeint_bbbbbbbb", AssertEx.NotNull(rotated.Principal).FindFirst(NodeAuthorizationPolicies.IntegrationKeyPrefixClaimType)?.Value);
    }

    [Test]
    public async Task Challenge_Writes401WithABearerRealmAndTheSameBodyForRevokedAndUnknownKeys()
    {
        // THE load-bearing assertion of ruling R2-6: a revoked credential and a never-issued one produce a
        // byte-identical rejection, so a caller can never learn that the key it holds was once real.
        await using var factory = CreateFactory();

        var (revokedStatus, revokedChallenge, revokedBody) = await ChallengeAsync(factory, "xeint_revoked-credential-material-here").ConfigureAwait(false);
        var (unknownStatus, unknownChallenge, unknownBody) = await ChallengeAsync(factory, "xeint_never-issued-credential-material").ConfigureAwait(false);

        AssertEx.Equal((int)HttpStatusCode.Unauthorized, revokedStatus);
        AssertEx.Equal((int)HttpStatusCode.Unauthorized, unknownStatus);
        AssertEx.Equal("Bearer realm=\"xe-local-ai-engine-integration\"", revokedChallenge);
        AssertEx.Equal(unknownChallenge ?? string.Empty, revokedChallenge);
        AssertEx.True(unknownBody.SequenceEqual(revokedBody));
        AssertEx.Equal(expected: 0, revokedBody.Length, "The challenge writes no body at all, so there is nothing to differ.");
    }

    [Test]
    public async Task IntegrationPolicy_IsSatisfiedByTheKeySchemeAndNeverByAnOperatorRole()
    {
        await using var factory = CreateFactory();
        var authenticated = await AuthenticateAsync(factory, ValidKey).ConfigureAwait(false);
        var authorization = factory.Services.GetRequiredService<IAuthorizationService>();

        var allowed = await authorization.AuthorizeAsync(AssertEx.NotNull(authenticated.Principal), resource: null, NodeAuthorizationPolicies.IntegrationApi)
                                         .ConfigureAwait(false);
        var operatorGate = await authorization.AuthorizeAsync(AssertEx.NotNull(authenticated.Principal), resource: null, NodeAuthorizationPolicies.Operator)
                                              .ConfigureAwait(false);

        AssertEx.True(allowed.Succeeded, "The key IS the authorization for this family.");
        AssertEx.False(operatorGate.Succeeded, "An integrator must never satisfy the operator policy.");
    }

    private static readonly Guid PrincipalId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static async Task<AuthenticateResult> AuthenticateAsync(TestServerWebAppFactory factory, string? presented)
    {
        // A fresh DI SCOPE per probe, not the root provider: AuthenticationHandler caches its authenticate result on
        // the handler instance, and the handler provider is scoped — sharing one scope would answer every later probe
        // with the first one's verdict.
        await using var scope = factory.Services.CreateAsyncScope();
        var context = CreateContext(scope);
        if (presented is not null)
        {
            context.Request.Headers.Authorization = $"Bearer {presented}";
        }

        return await context.AuthenticateAsync(IntegrationApiKeyAuthenticationHandler.SchemeName).ConfigureAwait(false);
    }

    private static async Task<(int StatusCode, string? WwwAuthenticate, byte[] Body)> ChallengeAsync(TestServerWebAppFactory factory, string presented)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var context = CreateContext(scope);
        context.Request.Headers.Authorization = $"Bearer {presented}";
        using var body = new MemoryStream();
        context.Response.Body = body;

        await context.ChallengeAsync(IntegrationApiKeyAuthenticationHandler.SchemeName).ConfigureAwait(false);

        return (context.Response.StatusCode, context.Response.Headers.WWWAuthenticate.ToString(), body.ToArray());
    }

    private static DefaultHttpContext CreateContext(AsyncServiceScope scope) =>
        new()
        {
            RequestServices = scope.ServiceProvider
        };

    /// <summary>
    ///     A host whose key service knows two live credentials sharing one principal, and treats everything else —
    ///     including the "revoked" probe — as the same <see langword="null" />, which is exactly what the real service
    ///     does for a revoked row.
    /// </summary>
    private static TestServerWebAppFactory CreateFactory()
    {
        var keyService = Substitute.For<IIntegrationApiKeyService>();
        _ = keyService.ValidateAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
                      .Returns(call => call.Arg<string?>() switch
                      {
                          ValidKey => new IntegrationApiKeyValidation(PrincipalId, "xeint_aaaaaaaa", AllowedTriggerIds: null),
                          RotatedKey => new IntegrationApiKeyValidation(PrincipalId, "xeint_bbbbbbbb", AllowedTriggerIds: null),
                          _ => null
                      });

        return new TestServerWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<IIntegrationApiKeyService>();
                services.AddScoped(_ => keyService);
            }
        };
    }
}

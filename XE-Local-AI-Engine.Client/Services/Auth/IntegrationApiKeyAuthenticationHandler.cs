namespace XE_Local_AI_Engine.Client.Services.Auth;

using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Integrations;

/// <summary>
///     Authenticates an EXTERNAL integrator against the node's hand-mapped integration API using one of the
///     operator-generated <c>xeint_</c> bearer keys. Registered as a FOURTH authentication scheme beside JWT bearer, the
///     MCP key and the model-proxy key, and applied only by the integration authorization policy, so every other
///     surface keeps its own posture untouched.
///     <para>
///         <b>Two claims, one authority.</b> The identity carries the integrator's <c>PrincipalId</c> and the
///         credential's display prefix. Every ownership, uniqueness and masking decision downstream reads the
///         PRINCIPAL; the prefix is attribution only, so an operator can still see which credential made a call. The
///         trigger allowlist is deliberately NOT a claim — the accept path and the access helper re-read the key row,
///         so re-scoping a key takes effect on the next request instead of at the next token mint.
///     </para>
///     <para>
///         <b>There is no 403 on this family.</b> A missing, malformed, unknown or REVOKED key are the same
///         <see cref="AuthenticateResult.Fail(string)" /> and the same 401 with no distinguishing body: telling a caller
///         its key was real but revoked would confirm it holds a genuine credential. Every authorisation-shaped outcome
///         further downstream — a trigger the key is not allowlisted for, another principal's execution — is a 404
///         identical to "unknown" (rulings R1-4 and R2-6).
///     </para>
///     <para>
///         <b>This is not the only gate.</b> The routes are mounted inside <c>/api/local/v1</c>, so
///         <c>LocalApiSecurityMiddleware</c> has already rejected any non-loopback peer, foreign Host or cross-origin
///         request before this handler runs. Mounting them outside that prefix would silently remove that layer and
///         leave this key as the ONLY control — don't.
///     </para>
/// </summary>
internal sealed class IntegrationApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    /// <summary>The scheme name. Referenced by <see cref="NodeAuthorizationPolicies.IntegrationApi" />.</summary>
    public const string SchemeName = "IntegrationApiKey";

    /// <summary>
    ///     The RFC 6750 challenge this scheme writes. A constant because the hand-mapped handler has to answer a
    ///     mid-request revocation with a BYTE-IDENTICAL 401, and two literals would eventually drift.
    /// </summary>
    internal const string BearerChallenge = "Bearer realm=\"xe-local-ai-engine-integration\"";

    private const string BearerPrefix = "Bearer ";

    private readonly IIntegrationApiKeyService _apiKeyService;

    public IntegrationApiKeyAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IIntegrationApiKeyService apiKeyService)
        : base(options, logger, encoder)
    {
        _apiKeyService = apiKeyService ?? throw new ArgumentNullException(nameof(apiKeyService));
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(header))
        {
            // NoResult, not Fail: "no credential presented" is not an authentication error, and returning Fail here
            // would bypass the challenge that emits the WWW-Authenticate header clients need.
            return AuthenticateResult.NoResult();
        }

        if (!header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.Fail("Unsupported authorization scheme.");
        }

        // Never log the header, the presented value or any substring of it beyond the display prefix the service
        // returns on success.
        var presented = header[BearerPrefix.Length..].Trim();
        var validation = await _apiKeyService.ValidateAsync(presented, Context.RequestAborted).ConfigureAwait(false);
        if (validation is null)
        {
            return AuthenticateResult.Fail("Invalid integration API key.");
        }

        // The integrator is NOT the node operator and must never be mistaken for one: no role claim, so the Operator
        // policy (which requires the Admin role) can never be satisfied by this scheme.
        var identity = new ClaimsIdentity([
            new Claim(ClaimTypes.Name, "integration-client"),
            new Claim(NodeAuthorizationPolicies.IntegrationPrincipalClaimType, validation.PrincipalId.ToString("D")),
            new Claim(NodeAuthorizationPolicies.IntegrationKeyPrefixClaimType, validation.KeyPrefix)
        ], SchemeName);
        var principal = new ClaimsPrincipal(identity);
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        // RFC 6750 bearer challenge. No OAuth / resource-metadata parameter: this surface accepts only a static
        // pre-shared key, so advertising a discovery document that does not exist would only mislead a client.
        Response.Headers.WWWAuthenticate = BearerChallenge;
        return Task.CompletedTask;
    }
}

namespace XE_Local_AI_Engine.Client.Services.Auth;

using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Proxy;

/// <summary>
///     Authenticates an EXTERNAL tool against the node's inbound OpenAI-compatible model proxy using the single
///     operator-generated bearer key. Registered as a THIRD authentication scheme beside JWT bearer and the MCP key,
///     and applied only by the model-proxy authorization policy, so the browser-facing surface keeps its JWT/Operator
///     posture and the MCP surface keeps its own key untouched.
///     <para>
///         <b>Why a bearer key.</b> External OpenAI-compatible clients (LiteLLM, Continue, a Hermes-style agent) already
///         accept a static <c>Authorization: Bearer …</c> header as the API key — it is the OpenAI wire convention — so
///         a pre-shared bearer over a loopback-only, single-user surface is both the proportionate control and the one
///         these clients speak natively.
///     </para>
///     <para>
///         <b>This is not the only gate.</b> The endpoints are mounted inside <c>/api/local/v1</c>, so
///         <c>LocalApiSecurityMiddleware</c> has already rejected any non-loopback peer, foreign Host or cross-origin
///         request before this handler runs. Mounting them outside that prefix would silently remove that layer and
///         leave this key as the ONLY control — don't.
///     </para>
/// </summary>
internal sealed class LocalModelProxyApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    /// <summary>The scheme name. Referenced by <see cref="NodeAuthorizationPolicies.LocalModelProxy" />.</summary>
    public const string SchemeName = "LocalModelProxyApiKey";

    private const string BearerPrefix = "Bearer ";

    private readonly ILocalModelProxyApiKeyService _apiKeyService;

    public LocalModelProxyApiKeyAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        ILocalModelProxyApiKeyService apiKeyService)
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

        var presented = header[BearerPrefix.Length..].Trim();
        if (!await _apiKeyService.ValidateAsync(presented, Context.RequestAborted).ConfigureAwait(false))
        {
            // Deliberately uniform: never distinguish "no key generated" from "wrong key" to a caller.
            return AuthenticateResult.Fail("Invalid model-proxy API key.");
        }

        // The external tool is NOT the node operator and must never be mistaken for one: it gets its own identity with
        // no role claim, so the Operator policy (which requires the Admin role) can never be satisfied by this scheme.
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "local-model-proxy-client")], SchemeName);
        var principal = new ClaimsPrincipal(identity);
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        // RFC 6750 bearer challenge. No OAuth / resource-metadata parameter: this proxy accepts only a static pre-shared
        // key, exactly as OpenAI-compatible clients expect, so advertising a discovery document that does not exist
        // would only mislead a client.
        Response.Headers.WWWAuthenticate = "Bearer realm=\"xe-local-ai-engine-model-proxy\"";
        return Task.CompletedTask;
    }
}

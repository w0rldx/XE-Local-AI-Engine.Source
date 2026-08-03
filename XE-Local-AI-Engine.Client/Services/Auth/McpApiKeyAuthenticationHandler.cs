namespace XE_Local_AI_Engine.Client.Services.Auth;

using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Mcp;

/// <summary>
///     Authenticates an EXTERNAL MCP client against the node's inbound MCP endpoint using the single operator-generated
///     bearer key. Registered as a SECOND authentication scheme beside JWT bearer and applied only by the MCP
///     authorization policy, so the browser-facing surface keeps its JWT/Operator posture untouched.
///     <para>
///         <b>Why a bearer key and not OAuth.</b> MCP specification revision 2026-07-28 states that authorization is
///         OPTIONAL for MCP implementations; the OAuth 2.1 / RFC 9728 profile binds only an implementation that opts
///         into it. This node deliberately does not, and therefore advertises no Protected Resource Metadata — a
///         pre-shared bearer over a loopback-only, single-user surface is the proportionate control, and it is what
///         external clients accept directly (Claude Code: <c>--header "Authorization: Bearer …"</c>).
///     </para>
///     <para>
///         <b>This is not the only gate.</b> The endpoint is mounted inside <c>/api/local/v1</c>, so
///         <c>LocalApiSecurityMiddleware</c> has already rejected any non-loopback peer, foreign Host or cross-origin
///         request before this handler runs. Mounting the MCP endpoint outside that prefix would silently remove that
///         layer and leave this key as the ONLY control — don't.
///     </para>
/// </summary>
internal sealed class McpApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    /// <summary>The scheme name. Referenced by <see cref="NodeAuthorizationPolicies.McpServer" />.</summary>
    public const string SchemeName = "McpApiKey";

    private const string BearerPrefix = "Bearer ";

    private readonly IMcpServerApiKeyService _apiKeyService;

    public McpApiKeyAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IMcpServerApiKeyService apiKeyService)
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
            return AuthenticateResult.Fail("Invalid MCP API key.");
        }

        // The MCP client is NOT the node operator and must never be mistaken for one: it gets its own identity with no
        // role claim, so the Operator policy (which requires the Admin role) can never be satisfied by this scheme.
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "mcp-client")], SchemeName);
        var principal = new ClaimsPrincipal(identity);
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        // RFC 6750 bearer challenge. No `resource_metadata` parameter: this server does not implement the spec's
        // optional OAuth profile, so advertising Protected Resource Metadata would point clients at a discovery
        // document that does not exist.
        Response.Headers.WWWAuthenticate = "Bearer realm=\"xe-local-ai-engine-mcp\"";
        return Task.CompletedTask;
    }
}

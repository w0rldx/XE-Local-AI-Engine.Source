namespace XE_Local_AI_Engine.Providers.CodexOAuth.Options;

using System.ComponentModel.DataAnnotations;
using System.Reflection;

/// <summary>
///     Non-secret configuration for the Codex OAuth model provider.
///     Contains endpoints, the public OAuth client id, the Codex header contract values, and timeouts.
///     Tokens are never stored here — they live encrypted in <see cref="Auth.CodexTokenStore" />.
/// </summary>
public sealed class CodexOptions
{
    public const string SectionName = "CodexOAuth";

    /// <summary>Honest product originator identifying this client to OpenAI. See <see cref="Originator" />.</summary>
    private const string ProductOriginator = "xe-local-ai-engine";

    /// <summary>
    ///     Honest product User-Agent (<c>XE-Local-AI-Engine/&lt;version&gt;</c>) built from this assembly's
    ///     informational version, falling back to its assembly version. Computed once. See <see cref="UserAgent" />.
    /// </summary>
    private static readonly string ProductUserAgent = BuildProductUserAgent();

    /// <summary>
    ///     Codex backend base URL (subscription auth, not api.openai.com). The Responses endpoint
    ///     is <c>{BaseUrl}/responses</c>.
    /// </summary>
    [Required]
    public Uri BaseUrl { get; set; } = new("https://chatgpt.com/backend-api/codex", UriKind.Absolute);

    /// <summary>OAuth authorize endpoint (PKCE S256 browser flow). Configurable.</summary>
    /// <remarks>
    ///     LIVE-CORRECTNESS: the authorize host is the OAuth ISSUER (<c>auth.openai.com</c>), NOT <c>chatgpt.com</c>,
    ///     verified against the working opencode reference client. Using <c>chatgpt.com/oauth/authorize</c> makes live
    ///     sign-in fail.
    /// </remarks>
    [Required]
    public Uri AuthorizeUrl { get; set; } = new("https://auth.openai.com/oauth/authorize", UriKind.Absolute);

    /// <summary>OAuth token endpoint (code exchange and refresh).</summary>
    [Required]
    public Uri TokenUrl { get; set; } = new("https://auth.openai.com/oauth/token", UriKind.Absolute);

    /// <summary>Public OAuth client id observed from the Codex CLI. Not a secret.</summary>
    [Required]
    public string ClientId { get; set; } = "app_EMoamEEZ73f0CkXaXp7hrann";

    /// <summary>Loopback port the local PKCE callback listener binds to; Codex uses 1455.</summary>
    /// <remarks>The listener is loopback-only.</remarks>
    public int CallbackPort { get; set; } = 1455;

    /// <summary>Loopback redirect path the authorization server calls back to.</summary>
    /// <remarks>
    ///     LIVE-CORRECTNESS: must be <c>/auth/callback</c>, because the registered Codex client
    ///     (<see cref="ClientId" />) permits only the exact redirect URI
    ///     <c>http://localhost:1455/auth/callback</c>, matching the Codex CLI and opencode. A different path makes the
    ///     authorize request fail with OpenAI <c>unknown_error</c>.
    /// </remarks>
    public string CallbackPath { get; set; } = "/auth/callback";

    /// <summary>OAuth scope requested during the authorize step. Configurable.</summary>
    /// <remarks>
    ///     LIVE-CORRECTNESS: matches the working opencode reference client's exact scope
    ///     (<c>openid profile email offline_access</c>), and <c>offline_access</c> is what yields a refresh token.
    /// </remarks>
    public string Scope { get; set; } = "openid profile email offline_access";

    /// <summary>Codex <c>originator</c> header value identifying the client family to OpenAI.</summary>
    /// <remarks>
    ///     The default is honest and product-specific (<c>xe-local-ai-engine</c>); the operator chose this over
    ///     impersonating the official Codex CLI, on ToS grounds. Bound from the <c>CodexOAuth</c> config section, so it
    ///     is operator-overridable without a recompile. NOTE: the subscription Responses endpoint MAY require a
    ///     Codex-compatible originator; if live sign-in returns 4xx on chat, override this and
    ///     <see cref="UserAgent" />.
    /// </remarks>
    public string Originator { get; set; } = ProductOriginator;

    /// <summary><c>User-Agent</c> header value sent on the SSE Responses path, identifying this client to OpenAI.</summary>
    /// <remarks>
    ///     The default is honest and product-specific (<c>XE-Local-AI-Engine/&lt;asm-version&gt;</c>); the operator
    ///     chose this over impersonating the official Codex CLI, on ToS grounds. Bound from the <c>CodexOAuth</c>
    ///     config section, so it is operator-overridable without a recompile. NOTE: the subscription Responses endpoint
    ///     MAY require a Codex-compatible User-Agent; if live sign-in returns 4xx on chat, override this and
    ///     <see cref="Originator" />.
    /// </remarks>
    public string UserAgent { get; set; } = ProductUserAgent;

    /// <summary>
    ///     Default Codex model id (account-scoped <c>gpt-5.x</c> family); the node's selection store persists the
    ///     non-secret selected model, and this is the fallback.
    /// </summary>
    /// <remarks>
    ///     LIVE-CORRECTNESS: must be a model the subscription Responses backend accepts. Any id outside
    ///     <see cref="Implementation.CodexModelCatalog.ModelIds" /> — the earlier default <c>gpt-5-codex</c>, for
    ///     instance — is rejected with HTTP 400. <c>gpt-5.6-sol</c> is the frontier model and leads the catalog, but
    ///     the chosen default is <c>gpt-5.6-terra</c>; operators can override either via the <c>CodexOAuth</c> section.
    /// </remarks>
    public string DefaultModel { get; set; } = "gpt-5.6-terra";

    /// <summary>Maximum time to wait for the user to complete the browser authorization.</summary>
    public TimeSpan LoginTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>HTTP timeout for the OAuth token endpoint (code exchange / refresh).</summary>
    public TimeSpan TokenRequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     Skew applied when deciding whether the access token is expired, so a near-expiry token is refreshed
    ///     proactively rather than failing mid-request.
    /// </summary>
    public TimeSpan ExpirySkew { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    ///     The loopback redirect URI derived from <see cref="CallbackPort" /> and <see cref="CallbackPath" />.
    /// </summary>
    public Uri RedirectUri => new($"http://localhost:{CallbackPort}{CallbackPath}", UriKind.Absolute);

    /// <summary>
    ///     Builds the honest product User-Agent token (<c>XE-Local-AI-Engine/&lt;version&gt;</c>), a valid
    ///     product/version token.
    /// </summary>
    /// <remarks>
    ///     Prefers the assembly's informational version, stripping any build metadata after a '+', then the assembly
    ///     version, then a static default.
    /// </remarks>
    private static string BuildProductUserAgent()
    {
        var assembly = typeof(CodexOptions).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var version = informational is { Length: > 0 }
            ? informational.Split(separator: '+', count: 2)[0]
            : assembly.GetName().Version?.ToString() ?? "1.0.0";
        return $"XE-Local-AI-Engine/{version}";
    }
}

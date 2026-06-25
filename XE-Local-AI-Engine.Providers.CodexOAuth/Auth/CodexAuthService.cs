namespace XE_Local_AI_Engine.Providers.CodexOAuth.Auth;

using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Providers.CodexOAuth.Options;

/// <summary>
///     Contract for the Codex OAuth login / refresh lifecycle.
/// </summary>
public interface ICodexAuthService
{
    /// <summary>
    ///     Starts the interactive PKCE (S256) loopback login: binds the loopback callback listener, builds the
    ///     authorize URL, and begins waiting for the callback in the background. The returned
    ///     <see cref="CodexLoginHandle" /> exposes the authorize URL <em>immediately</em> so the React client can render
    ///     it as a user-clicked link, and a
    ///     <see cref="CodexLoginHandle.Completion" /> task that resolves once the code is exchanged and persisted.
    /// </summary>
    CodexLoginHandle BeginLogin(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Runs the interactive PKCE (S256) login against a loopback callback listener,
    ///     exchanges the authorization code, persists the session, and returns it. Convenience wrapper over
    ///     <see cref="BeginLogin" /> that awaits completion.
    /// </summary>
    Task<CodexTokens> LoginAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Exchanges the current refresh token for a new session (<c>grant_type=refresh_token</c>) and persists it.
    /// </summary>
    Task<CodexTokens> RefreshAsync(CodexTokens current, CancellationToken cancellationToken = default);
}

/// <summary>
///     Implements the Codex OAuth 2.0 Authorization-Code + PKCE (S256) flow with a loopback callback.
///     Decodes the <c>chatgpt_account_id</c> JWT claim. Never logs token values.
/// </summary>
public sealed class CodexAuthService : ICodexAuthService
{
    private const string AccountClaimNamespace = "https://api.openai.com/auth";
    private const string AccountClaimName = "chatgpt_account_id";

    // Unix-second bounds DateTimeOffset.FromUnixTimeSeconds accepts; an out-of-range or malformed exp falls back.
    private const long MinUnixSeconds = -62135596800L;
    private const long MaxUnixSeconds = 253402300799L;
    private readonly HttpClient _httpClient;
    private readonly ILogger<CodexAuthService> _logger;

    private readonly CodexOptions _options;
    private readonly ICodexTokenStore _tokenStore;

    public CodexAuthService(IOptions<CodexOptions> options,
        HttpClient httpClient,
        ICodexTokenStore tokenStore,
        ILogger<CodexAuthService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(tokenStore);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options.Value;
        _httpClient = httpClient;
        _tokenStore = tokenStore;
        _logger = logger;
    }

    public CodexLoginHandle BeginLogin(CancellationToken cancellationToken = default)
    {
        var verifier = CreateCodeVerifier();
        var challenge = CreateCodeChallenge(verifier);
        var state = CreateState();

        var listener = new HttpListener();
        // Loopback-only binding. Started synchronously so the port is bound before we return the URL.
        listener.Prefixes.Add($"http://localhost:{_options.CallbackPort}{EnsureTrailingSlash(_options.CallbackPath)}");
        try
        {
            listener.Start();
        }
        catch (HttpListenerException exception)
        {
            // The loopback callback port could not be bound — e.g. a prior login left it half-open, or another
            // process holds it. Free this listener and surface a clean typed error instead of leaking it.
            ((IDisposable)listener).Dispose();
            throw new CodexAuthException($"Could not bind the Codex loopback callback port {_options.CallbackPort}. Close any prior sign-in and retry.",
                exception);
        }

        var authorizeUrl = BuildAuthorizeUrl(challenge, state);

        // The callback wait + code exchange runs in the background; the endpoint returns the URL immediately.
        var completion = CompleteLoginAsync(listener, verifier, state, cancellationToken);
        return new CodexLoginHandle(authorizeUrl, completion);
    }

    public async Task<CodexTokens> LoginAsync(CancellationToken cancellationToken = default)
    {
        return await BeginLogin(cancellationToken).Completion.ConfigureAwait(false);
    }

    public async Task<CodexTokens> RefreshAsync(CodexTokens current, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(current);

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.TokenUrl)
        {
            Content = JsonContent.Create(new
            {
                grant_type = "refresh_token",
                client_id = _options.ClientId,
                refresh_token = current.RefreshToken,
                scope = _options.Scope
            })
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.TokenRequestTimeout);

        var tokens = await SendTokenRequestAsync(request, current.RefreshToken, timeout.Token).ConfigureAwait(false);
        await _tokenStore.SaveAsync(tokens, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Codex OAuth token refreshed for account {AccountId}.", tokens.AccountId);
        return tokens;
    }

    private async Task<CodexTokens> CompleteLoginAsync(HttpListener listener,
        string verifier,
        string state,
        CancellationToken cancellationToken)
    {
        try
        {
            using var loginTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            loginTimeout.CancelAfter(_options.LoginTimeout);

            var code = await WaitForCallbackAsync(listener, state, loginTimeout.Token).ConfigureAwait(false);
            var tokens = await ExchangeCodeAsync(code, verifier, cancellationToken).ConfigureAwait(false);
            await _tokenStore.SaveAsync(tokens, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Codex OAuth login completed for account {AccountId}.", tokens.AccountId);
            return tokens;
        }
        finally
        {
            // Stops the listener and frees the loopback port whether login succeeded, timed out, or was superseded.
            listener.Close();
        }
    }

    private async Task<CodexTokens> ExchangeCodeAsync(string code, string verifier, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _options.TokenUrl)
        {
            Content = JsonContent.Create(new
            {
                grant_type = "authorization_code",
                client_id = _options.ClientId,
                code,
                redirect_uri = _options.RedirectUri.ToString(),
                code_verifier = verifier
            })
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.TokenRequestTimeout);

        return await SendTokenRequestAsync(request, refreshFallback: null, timeout.Token).ConfigureAwait(false);
    }

    private async Task<CodexTokens> SendTokenRequestAsync(HttpRequestMessage request,
        string? refreshFallback,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // Never include the response body — it may echo token material.
            throw new CodexAuthException($"Codex token endpoint returned {(int)response.StatusCode} ({response.StatusCode}).");
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;

        var access = ReadString(root, "access_token")
                     ?? throw new CodexAuthException("Codex token response did not include an access token.");

        // OAuth refresh responses may omit a rotated refresh token; fall back to the previous one.
        var refresh = ReadString(root, "refresh_token") ?? refreshFallback
            ?? throw new CodexAuthException("Codex token response did not include a refresh token.");

        var expiresUtc = TryGetExpiresIn(root, out var expiresIn)
            ? DateTimeOffset.UtcNow.AddSeconds(expiresIn)
            : GetJwtExpiry(access);

        var accountId = ExtractAccountId(access);

        return new CodexTokens(access, refresh, expiresUtc, accountId);
    }

    private static async Task<string> WaitForCallbackAsync(HttpListener listener, string expectedState, CancellationToken cancellationToken)
    {
        var contextTask = listener.GetContextAsync();
        var completed = await Task.WhenAny(contextTask, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
        if (completed != contextTask)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        var context = await contextTask.ConfigureAwait(false);
        var query = context.Request.QueryString;
        var returnedState = query["state"];
        var code = query["code"];
        var error = query["error"];

        try
        {
            if (!string.IsNullOrEmpty(error))
            {
                await WriteCallbackResponseAsync(context.Response, "Codex login failed. You can close this window.").ConfigureAwait(false);
                throw new CodexAuthException($"Codex authorization returned an error: {error}.");
            }

            // Validate state to defend against CSRF on the loopback callback.
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(returnedState ?? string.Empty),
                    Encoding.UTF8.GetBytes(expectedState)))
            {
                await WriteCallbackResponseAsync(context.Response, "Codex login failed. You can close this window.").ConfigureAwait(false);
                throw new CodexAuthException("Codex authorization callback state did not match.");
            }

            if (string.IsNullOrEmpty(code))
            {
                await WriteCallbackResponseAsync(context.Response, "Codex login failed. You can close this window.").ConfigureAwait(false);
                throw new CodexAuthException("Codex authorization callback did not include a code.");
            }

            await WriteCallbackResponseAsync(context.Response, "Codex login complete. You can close this window.").ConfigureAwait(false);
            return code;
        }
        finally
        {
            context.Response.Close();
        }
    }

    private Uri BuildAuthorizeUrl(string challenge, string state)
    {
        var query = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["response_type"] = "code",
            ["client_id"] = _options.ClientId,
            ["redirect_uri"] = _options.RedirectUri.ToString(),
            ["scope"] = _options.Scope,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
            // LIVE-CORRECTNESS (verified against the working opencode reference client): identify the client
            // family on the authorize step, ask the issuer to embed the org/account id in the id_token (so the
            // subscription path can resolve chatgpt-account-id), and opt into the simplified Codex CLI flow.
            ["originator"] = _options.Originator,
            ["id_token_add_organizations"] = "true",
            ["codex_cli_simplified_flow"] = "true"
        };

        var builder = new UriBuilder(_options.AuthorizeUrl)
        {
            Query = string.Join(separator: '&', query.Select(kvp =>
                $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value ?? string.Empty)}"))
        };
        return builder.Uri;
    }

    private static async Task WriteCallbackResponseAsync(HttpListenerResponse response, string message)
    {
        var body = Encoding.UTF8.GetBytes($"<html><body><p>{message}</p></body></html>");
        response.ContentType = "text/html";

        // The callback URL carried the OAuth authorization code; prevent the browser from caching this response or
        // leaking a referrer so the code cannot linger in cache/history.
        response.Headers["Cache-Control"] = "no-store";
        response.Headers["Pragma"] = "no-cache";
        response.Headers["Referrer-Policy"] = "no-referrer";

        response.ContentLength64 = body.Length;
        await response.OutputStream.WriteAsync(body).ConfigureAwait(false);
    }

    private static string CreateCodeVerifier()
    {
        // RFC 7636: 43-128 chars from the unreserved set; 32 random bytes base64url-encoded yields 43 chars.
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Base64UrlEncode(bytes);
    }

    private static string CreateCodeChallenge(string verifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Base64UrlEncode(hash);
    }

    private static string CreateState()
    {
        return Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
    }

    private static string ExtractAccountId(string jwt)
    {
        var payload = DecodeJwtPayload(jwt);
        if (payload.TryGetProperty(AccountClaimNamespace, out var authClaim)
            && authClaim.TryGetProperty(AccountClaimName, out var accountId)
            && accountId.ValueKind == JsonValueKind.String)
        {
            return accountId.GetString()!;
        }

        throw new CodexAuthException("Codex access token did not contain a chatgpt_account_id claim.");
    }

    private static DateTimeOffset GetJwtExpiry(string jwt)
    {
        var payload = DecodeJwtPayload(jwt);

        // Guard against a malformed (non-integral / oversized) exp claim: TryGetInt64 + range-check so a hostile
        // or corrupt token can't throw or produce a nonsense expiry.
        if (payload.TryGetProperty("exp", out var exp)
            && exp.ValueKind == JsonValueKind.Number
            && exp.TryGetInt64(out var expSeconds)
            && expSeconds is >= MinUnixSeconds and <= MaxUnixSeconds)
        {
            return DateTimeOffset.FromUnixTimeSeconds(expSeconds);
        }

        // Conservative default if the token has no usable exp claim.
        return DateTimeOffset.UtcNow.AddMinutes(50);
    }

    // SECURITY: the JWT payload is base64url-decoded WITHOUT verifying the token signature. This is
    // intentional and safe here: the access token is received over TLS directly from the OpenAI token endpoint,
    // and the decoded claims (chatgpt_account_id, exp) are used ONLY as advisory metadata — the account id becomes
    // a request header and the expiry drives proactive refresh. NEITHER is ever used as an authorization input or
    // a trust decision on THIS node, so no signature validation is required. Do not repurpose these claims for
    // access control without first verifying the signature against OpenAI's JWKS.
    private static JsonElement DecodeJwtPayload(string jwt)
    {
        var segments = jwt.Split('.');
        if (segments.Length < 2)
        {
            throw new CodexAuthException("Codex access token is not a valid JWT.");
        }

        var payloadBytes = Base64UrlDecode(segments[1]);
        using var document = JsonDocument.Parse(payloadBytes);
        return document.RootElement.Clone();
    }

    private static bool TryGetExpiresIn(JsonElement root, out int expiresIn)
    {
        // Guard against a malformed / oversized expires_in: TryGetInt32 + non-negative bound so a corrupt value
        // can't throw or yield a negative lifetime. Out-of-range falls back to the JWT exp claim.
        if (root.TryGetProperty("expires_in", out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out var parsed)
            && parsed >= 0)
        {
            expiresIn = parsed;
            return true;
        }

        expiresIn = 0;
        return false;
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object
               && element.TryGetProperty(propertyName, out var value)
               && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace(oldChar: '+', newChar: '-').Replace(oldChar: '/', newChar: '_');
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace(oldChar: '-', newChar: '+').Replace(oldChar: '_', newChar: '/');
        padded = (padded.Length % 4) switch
        {
            2 => padded + "==",
            3 => padded + "=",
            _ => padded
        };
        return Convert.FromBase64String(padded);
    }

    private static string EnsureTrailingSlash(string path)
    {
        return path.EndsWith('/') ? path : path + "/";
    }
}

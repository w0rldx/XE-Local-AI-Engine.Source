namespace XE_Local_AI_Engine.Client.Services.AppUpdate;

using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

/// <summary>
///     GitHub App device-flow implementation. Talks to github.com over HTTPS using an injected <see cref="HttpClient" />
///     (the host supplies a named client). The device_code is never returned to React (the endpoint maps the start result
///     to a token-free DTO); the access token is persisted only via <see cref="IGitHubTokenStore" /> and is never logged
///     or echoed. There is no refresh path — the GitHub App user-token expiration is off by design; a server-side
///     revoke surfaces as a 401 on the next update check and is handled there as <c>reauthRequired</c>. Sign-out's
///     guaranteed effect is the local token deletion; the server-side revoke call is best-effort only (it is a no-op
///     without the App client_secret the device flow does not carry).
/// </summary>
public sealed class GitHubAuthService : IGitHubAuthService
{
    // GitHub device-flow + API endpoints. HTTPS only (an http override would let a downgrade leak the token). Modeled as
    // Uri constants (matching CodexOptions) so the URL literals are an explicit absolute-Uri specification.
    private static readonly Uri DeviceCodeUrl = new("https://github.com/login/device/code", UriKind.Absolute);
    private static readonly Uri AccessTokenUrl = new("https://github.com/login/oauth/access_token", UriKind.Absolute);
    private static readonly Uri UserUrl = new("https://api.github.com/user", UriKind.Absolute);

    // The api.github.com base the server-side token revoke targets (combined with the client_id at call time).
    private static readonly Uri ApiBaseUrl = new("https://api.github.com", UriKind.Absolute);

    // Fallback verification URL when GitHub omits verification_uri from the device-code response.
    private static readonly Uri DefaultVerificationUri = new("https://github.com/login/device", UriKind.Absolute);

    // A required User-Agent for api.github.com requests (GitHub rejects requests without one).
    private const string UserAgent = "XE-Local-AI-Engine-Updater";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly ILogger<GitHubAuthService> _logger;
    private readonly IOptions<AppUpdateChannelOptions> _options;
    private readonly IGitHubTokenStore _tokenStore;

    public GitHubAuthService(IHttpClientFactory httpClientFactory,
        IGitHubTokenStore tokenStore,
        IOptions<AppUpdateChannelOptions> options,
        ILogger<GitHubAuthService> logger)
        : this(CreateClient(httpClientFactory), tokenStore, options, logger)
    {
    }

    // Test seam: injects the HttpClient directly so the device flow can be exercised against a mocked handler.
    internal GitHubAuthService(HttpClient httpClient,
        IGitHubTokenStore tokenStore,
        IOptions<AppUpdateChannelOptions> options,
        ILogger<GitHubAuthService> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>The named <see cref="HttpClient" /> the host registers for GitHub auth.</summary>
    public const string HttpClientName = "GitHubDeviceFlow";

    /// <inheritdoc />
    public async Task<GitHubDeviceFlowStart> StartAsync(CancellationToken ct)
    {
        var clientId = RequireClientId();

        using var request = new HttpRequestMessage(HttpMethod.Post, DeviceCodeUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        // GitHub App device flow: the body is client_id only. Token permissions are governed by the GitHub App's
        // fine-grained configuration (contents:read + the forced metadata:read), NOT by an OAuth `scope` param — that
        // param belongs to legacy OAuth Apps and would be ignored here, so it is intentionally omitted.
        request.Content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("client_id", clientId)
        });

        DeviceCodeResponse? body;
        try
        {
            using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new GitHubAuthException("GitHub could not start the sign-in. Please try again.");
            }

            body = await response.Content.ReadFromJsonAsync<DeviceCodeResponse>(SerializerOptions, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new GitHubAuthException("GitHub could not be reached to start sign-in.", exception);
        }

        if (body is null || string.IsNullOrWhiteSpace(body.DeviceCode) || string.IsNullOrWhiteSpace(body.UserCode))
        {
            throw new GitHubAuthException("GitHub returned an incomplete sign-in response.");
        }

        // Default the polling interval to GitHub's documented 5s floor when absent, and the validity to 15 min.
        return new GitHubDeviceFlowStart(body.DeviceCode,
            body.UserCode,
            string.IsNullOrWhiteSpace(body.VerificationUri) ? DefaultVerificationUri.ToString() : body.VerificationUri,
            body.ExpiresInSeconds > 0 ? body.ExpiresInSeconds : 900,
            body.IntervalSeconds > 0 ? body.IntervalSeconds : 5);
    }

    /// <inheritdoc />
    public async Task<GitHubDeviceFlowPoll> PollAsync(string deviceCode, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceCode);
        var clientId = RequireClientId();

        using var request = new HttpRequestMessage(HttpMethod.Post, AccessTokenUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("client_id", clientId),
            new KeyValuePair<string, string>("device_code", deviceCode),
            new KeyValuePair<string, string>("grant_type", "urn:ietf:params:oauth:grant-type:device_code")
        });

        AccessTokenResponse? body;
        try
        {
            using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            body = await response.Content.ReadFromJsonAsync<AccessTokenResponse>(SerializerOptions, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new GitHubAuthException("GitHub could not be reached to complete sign-in.", exception);
        }

        if (body is null)
        {
            throw new GitHubAuthException("GitHub returned an empty sign-in response.");
        }

        // The device-flow "errors" are normal control flow, not failures — map them to poll states.
        if (!string.IsNullOrWhiteSpace(body.Error))
        {
            return body.Error switch
            {
                "authorization_pending" or "slow_down" => new GitHubDeviceFlowPoll(GitHubDeviceFlowState.Pending, Login: null),
                "access_denied" => new GitHubDeviceFlowPoll(GitHubDeviceFlowState.Denied, Login: null),
                "expired_token" => new GitHubDeviceFlowPoll(GitHubDeviceFlowState.Expired, Login: null),
                _ => throw new GitHubAuthException("GitHub rejected the sign-in. Please start over.")
            };
        }

        if (string.IsNullOrWhiteSpace(body.AccessToken))
        {
            throw new GitHubAuthException("GitHub returned no access token. Please start over.");
        }

        var login = await ResolveLoginAsync(body.AccessToken, ct).ConfigureAwait(false);
        await _tokenStore.SetSessionAsync(new GitHubSession(body.AccessToken, login), ct).ConfigureAwait(false);

        return new GitHubDeviceFlowPoll(GitHubDeviceFlowState.Authorized, login);
    }

    /// <inheritdoc />
    public async Task SignOutAsync(CancellationToken ct)
    {
        var session = await _tokenStore.GetSessionAsync(ct).ConfigureAwait(false);

        // Attempt a best-effort server-side revoke first, then always clear the local copy. NOTE: the guaranteed effect
        // of sign-out is the local token deletion below — DELETE /applications/{client_id}/token requires HTTP Basic
        // auth with the App client_secret, which the device flow does not carry, so without a wired client_secret the
        // remote call is a no-op GitHub rejects. The call is kept because it works if a client_secret is ever supplied.
        // A remote failure never throws; the local store is cleared regardless (offline / already-revoked).
        if (session is not null && _options.Value.IsConfigured)
        {
            await RevokeServerSideBestEffortAsync(session.AccessToken, ct).ConfigureAwait(false);
        }

        await _tokenStore.ClearSessionAsync(ct).ConfigureAwait(false);
    }

    private async Task RevokeServerSideBestEffortAsync(string accessToken, CancellationToken ct)
    {
        var clientId = _options.Value.GitHubAppClientId;
        var revokeUrl = new Uri(ApiBaseUrl, $"/applications/{Uri.EscapeDataString(clientId)}/token");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, revokeUrl)
            {
                Content = JsonContent.Create(new RevokeTokenRequest(accessToken), options: SerializerOptions)
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.UserAgent.ParseAdd(UserAgent);

            using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // Don't surface the token or the status detail — log only that the remote revoke did not succeed.
                _logger.LogWarning("Server-side GitHub token revoke did not succeed; the local session was still cleared.");
            }
        }
        catch (HttpRequestException)
        {
            _logger.LogWarning("Server-side GitHub token revoke could not be reached; the local session was still cleared.");
        }
    }

    private async Task<string> ResolveLoginAsync(string accessToken, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, UserUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.UserAgent.ParseAdd(UserAgent);

            using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return "unknown";
            }

            var user = await response.Content.ReadFromJsonAsync<GitHubUserResponse>(SerializerOptions, ct).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(user?.Login) ? "unknown" : user.Login;
        }
        catch (HttpRequestException)
        {
            // The login is display-only; a failure to resolve it must not block a successful sign-in.
            return "unknown";
        }
    }

    private string RequireClientId()
    {
        // Gate on AppUpdateChannelOptions.IsConfigured — the SAME predicate AppUpdateService uses to go inert — not on a
        // bare emptiness check. A weaker check here splits the build in two: the update check would report a signed-out,
        // inert updater while this device flow happily POSTed a structurally invalid client_id (a placeholder, an OAuth
        // App id, the numeric App ID) to github.com and surfaced the result as a transport failure. Signing in is also
        // pointless when the repo URL is unbaked, since nothing can be read from it afterwards.
        var options = _options.Value;
        if (!options.IsConfigured)
        {
            throw new GitHubAuthException("App self-update is not configured for this build.");
        }

        return options.GitHubAppClientId;
    }

    private static HttpClient CreateClient(IHttpClientFactory httpClientFactory)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        return httpClientFactory.CreateClient(HttpClientName);
    }

    private sealed record DeviceCodeResponse(
        [property: JsonPropertyName("device_code")]
        string? DeviceCode,
        [property: JsonPropertyName("user_code")]
        string? UserCode,
        [property: JsonPropertyName("verification_uri")]
        string? VerificationUri,
        [property: JsonPropertyName("expires_in")]
        int ExpiresInSeconds,
        [property: JsonPropertyName("interval")]
        int IntervalSeconds);

    private sealed record AccessTokenResponse(
        [property: JsonPropertyName("access_token")]
        string? AccessToken,
        [property: JsonPropertyName("token_type")]
        string? TokenType,
        [property: JsonPropertyName("scope")]
        string? Scope,
        [property: JsonPropertyName("error")]
        string? Error);

    private sealed record GitHubUserResponse(
        [property: JsonPropertyName("login")]
        string? Login);

    private sealed record RevokeTokenRequest(
        [property: JsonPropertyName("access_token")]
        string AccessToken);
}

namespace XE_Local_AI_Engine.Cli.Auth;

using System.Net;
using System.Net.Http.Json;
using XE_Local_AI_Engine.Cli.Api;

/// <summary>
///     Signs the CLI in with body-mode tokens and keeps a valid access token: refresh through the
///     <c>X-XE-Refresh-Token</c> header before expiry or after a 401, then re-login when a password provider is given.
/// </summary>
/// <remarks>
///     Tokens live in memory, and in the 0600 token file only when the caller passes a path. Neither the password nor
///     a token is ever logged or put in an exception message.
/// </remarks>
internal sealed class NodeAuthClient : IDisposable
{
    internal const string RefreshTokenHeaderName = "X-XE-Refresh-Token";

    internal const string LockedVault = "locked";

    /// <summary>How long before expiry an access token is refreshed rather than sent.</summary>
    internal static readonly TimeSpan RefreshSkew = TimeSpan.FromSeconds(30);

    /// <summary>The wait between status probes while the unlocked node replaces its locked pre-host on the same port.</summary>
    internal static readonly TimeSpan UnlockPollInterval = TimeSpan.FromMilliseconds(250);

    private const int UnlockPollAttempts = 120;

    private readonly HttpClient _http;
    private readonly string _origin;
    private readonly TimeProvider _timeProvider;
    private readonly string? _tokenFilePath;
    private readonly Func<CancellationToken, Task<string?>>? _passwordProvider;
    private readonly SemaphoreSlim _gate = new(initialCount: 1, maxCount: 1);
    private TokenPair? _tokens;
    private bool _tokenFileRead;

    /// <param name="http">A client from <see cref="NodeHttp.CreateClient" />; the caller owns it.</param>
    /// <param name="tokenFilePath">Where to keep the pair between runs, or null to keep it in memory only.</param>
    /// <param name="passwordProvider">Asked for the admin password when no live refresh token is left; null means never.</param>
    public NodeAuthClient(HttpClient http,
        Uri origin,
        TimeProvider timeProvider,
        string? tokenFilePath,
        Func<CancellationToken, Task<string?>>? passwordProvider)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _http = http;
        _origin = origin.GetLeftPart(UriPartial.Authority);
        _timeProvider = timeProvider;
        _tokenFilePath = tokenFilePath;
        _passwordProvider = passwordProvider;
    }

    public async Task<NodeAuthStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(new Uri("auth/status", UriKind.Relative), cancellationToken);
        return await NodeHttp.ReadJsonAsync<NodeAuthStatus>(response, cancellationToken);
    }

    public async Task LoginAsync(string password, string? email, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await LoginCoreAsync(password, email, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Unlocks a locked node with the admin password, then waits until the unlocked host answers on the same origin.</summary>
    public async Task UnlockVaultAsync(string password, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        using (var response = await _http.PostAsJsonAsync(new Uri("auth/vault/unlock", UriKind.Relative),
                   new VaultUnlockRequest
                   {
                       Password = password
                   },
                   NodeHttp.Json,
                   cancellationToken))
        {
            await NodeHttp.EnsureSuccessAsync(response, cancellationToken);
        }

        for (var attempt = 0; attempt < UnlockPollAttempts; attempt++)
        {
            await Task.Delay(UnlockPollInterval, _timeProvider, cancellationToken);
            try
            {
                if (!string.Equals((await GetStatusAsync(cancellationToken)).Vault, LockedVault, StringComparison.Ordinal))
                {
                    return;
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or NodeApiException { StatusCode: HttpStatusCode.ServiceUnavailable })
            {
                // The port is between the stopped pre-host and the starting real host.
            }
        }

        throw new NodeApiException(HttpStatusCode.ServiceUnavailable, "The node did not come back unlocked in time.");
    }

    /// <summary>A live access token: the cached one, else a refreshed one, else a fresh sign-in through the password provider.</summary>
    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_tokenFileRead && _tokens is null && _tokenFilePath is not null)
            {
                _tokens = await TokenFileStore.LoadAsync(_tokenFilePath, new Uri(_origin), cancellationToken);
            }

            _tokenFileRead = true;
            if (_tokens is { } current && current.ExpiresAtUtc - RefreshSkew > _timeProvider.GetUtcNow())
            {
                return current.AccessToken;
            }

            return await RenewCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Called after the node rejected <paramref name="rejectedToken" />; renews once even when several callers report it.</summary>
    public async Task<string> RenewAccessTokenAsync(string rejectedToken, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_tokens is { } current && !string.Equals(current.AccessToken, rejectedToken, StringComparison.Ordinal))
            {
                return current.AccessToken;
            }

            return await RenewCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() =>
        _gate.Dispose();

    private async Task<string> RenewCoreAsync(CancellationToken cancellationToken)
    {
        if (await RefreshCoreAsync(cancellationToken) is { } refreshed)
        {
            return refreshed;
        }

        // Another xe process may have rotated the pair through the same file, revoking ours: adopt its pair once.
        if (_tokenFilePath is not null
            && await TokenFileStore.LoadAsync(_tokenFilePath, new Uri(_origin), cancellationToken) is { } stored
            && !string.Equals(stored.RefreshToken, _tokens?.RefreshToken, StringComparison.Ordinal))
        {
            _tokens = stored;
            if (stored.ExpiresAtUtc - RefreshSkew > _timeProvider.GetUtcNow())
            {
                return stored.AccessToken;
            }

            if (await RefreshCoreAsync(cancellationToken) is { } adopted)
            {
                return adopted;
            }
        }

        _tokens = null;
        if (_passwordProvider is not null && await _passwordProvider(cancellationToken) is { Length: > 0 } password)
        {
            return await LoginCoreAsync(password, email: null, cancellationToken);
        }

        throw new NodeApiException(HttpStatusCode.Unauthorized, "Sign-in required: there is no session, or it has expired.");
    }

    /// <summary>The access token from refreshing the cached pair, or null when there is no live refresh token or the node rejected it.</summary>
    private async Task<string?> RefreshCoreAsync(CancellationToken cancellationToken)
    {
        if (_tokens is not { RefreshToken: { } refreshToken } tokens
            || tokens.RefreshTokenExpiresAtUtc <= _timeProvider.GetUtcNow())
        {
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("auth/refresh", UriKind.Relative));
        request.Headers.Add(RefreshTokenHeaderName, refreshToken);
        using var response = await _http.SendAsync(request, cancellationToken);
        return response.StatusCode == HttpStatusCode.Unauthorized
            ? null
            : await StoreAsync(await NodeHttp.ReadJsonAsync<TokenPair>(response, cancellationToken), cancellationToken);
    }

    private async Task<string> LoginCoreAsync(string password, string? email, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync(new Uri("auth/login", UriKind.Relative),
            new LoginRequest
            {
                Email = email,
                Password = password,
                ReturnRefreshToken = true
            },
            NodeHttp.Json,
            cancellationToken);

        // A wrong password is a 401 with no body (the lockout 401 carries its own message, kept as the node wrote it);
        // the generic "node answered 401" would read like an expired session.
        if (response.StatusCode == HttpStatusCode.Unauthorized
            && (await response.Content.ReadAsByteArrayAsync(cancellationToken)).Length == 0)
        {
            throw new NodeApiException(HttpStatusCode.Unauthorized, "Sign-in failed: wrong email or password.");
        }

        return await StoreAsync(await NodeHttp.ReadJsonAsync<TokenPair>(response, cancellationToken), cancellationToken);
    }

    private async Task<string> StoreAsync(TokenPair issued, CancellationToken cancellationToken)
    {
        var tokens = new TokenPair
        {
            AccessToken = issued.AccessToken,
            ExpiresAtUtc = issued.ExpiresAtUtc,
            RefreshToken = issued.RefreshToken,
            RefreshTokenExpiresAtUtc = issued.RefreshTokenExpiresAtUtc,
            Origin = _origin
        };
        if (_tokenFilePath is not null)
        {
            await TokenFileStore.SaveAsync(_tokenFilePath, tokens, cancellationToken);
        }

        _tokens = tokens;
        return tokens.AccessToken;
    }
}

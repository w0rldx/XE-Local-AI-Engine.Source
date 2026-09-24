namespace XE_Local_AI_Engine.Client.Services.CloudProviders.Auth;

using System.Net;
using Microsoft.Identity.Client;

/// <summary>
///     Owns the pending Entra ID authorization-code sign-in lifecycle so the Operator endpoints can start a browser
///     sign-in, return the authorize URL immediately, and poll status until the redemption completes.
/// </summary>
/// <remarks>
///     The sign-in runs against a confidential client. A second <see cref="StartAsync" /> <em>supersedes</em> any
///     in-flight attempt, cancelling it and releasing its
///     loopback listener before binding a new one on the same port. Never logs token material or raw callback
///     content. Mirrors <see cref="EntraDeviceCodeSignInCoordinator" />.
/// </remarks>
public sealed class EntraAuthCodeSignInCoordinator : IEntraAuthCodeSignInCoordinator, IDisposable
{
    private readonly IEntraAuthCodeAccountStore _accountStore;
    private readonly ICloudCredentialStore _credentialStore;
    private readonly Lock _gate = new();
    private readonly IEntraLiveCredentialCache _liveCredentialCache;
    private readonly ILogger<EntraAuthCodeSignInCoordinator> _logger;
    private readonly Action? _onSignInSucceeded;
    private readonly IEntraAuthCodeRedeemer _redeemer;
    private readonly TimeProvider _timeProvider;

    private CancellationTokenSource? _pendingCts;
    private LoopbackAuthorizationCodeListener? _pendingListener;
    private EntraAuthCodeSignInStatus _status = EntraAuthCodeSignInStatus.None;

    /// <param name="credentialStore">Reads the stored Azure Foundry connection's tenant / client / secret / redirect URI.</param>
    /// <param name="accountStore">Persists the MSAL home-account-id on success.</param>
    /// <param name="liveCredentialCache">
    ///     Keeps the authenticated delegated credential alive for the process lifetime so the chat-client factory
    ///     reuses it; the rationale is on <see cref="EntraDeviceCodeSignInCoordinator" />.
    /// </param>
    /// <param name="redeemer">The (fakeable) MSAL authorization-code redemption seam. See <see cref="IEntraAuthCodeRedeemer" />.</param>
    /// <param name="logger">Never receives token material.</param>
    /// <param name="timeProvider">Clock used to stamp the pending sign-in's callback deadline.</param>
    /// <param name="onSignInSucceeded">
    ///     Optional; runs once a credential is persisted. The host wires it to invalidate the active-cloud snapshot,
    ///     so a sign-in takes effect on the next send.
    /// </param>
    public EntraAuthCodeSignInCoordinator(ICloudCredentialStore credentialStore,
        IEntraAuthCodeAccountStore accountStore,
        IEntraLiveCredentialCache liveCredentialCache,
        IEntraAuthCodeRedeemer redeemer,
        ILogger<EntraAuthCodeSignInCoordinator> logger,
        TimeProvider timeProvider,
        Action? onSignInSucceeded = null)
    {
        ArgumentNullException.ThrowIfNull(credentialStore);
        ArgumentNullException.ThrowIfNull(accountStore);
        ArgumentNullException.ThrowIfNull(liveCredentialCache);
        ArgumentNullException.ThrowIfNull(redeemer);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _credentialStore = credentialStore;
        _accountStore = accountStore;
        _liveCredentialCache = liveCredentialCache;
        _redeemer = redeemer;
        _logger = logger;
        _timeProvider = timeProvider;
        _onSignInSucceeded = onSignInSucceeded;
    }

    /// <inheritdoc />
    public async Task<EntraAuthCodeSignInHandle> StartAsync(CancellationToken cancellationToken)
    {
        var connection = await LoadConnectionOrThrowAsync(cancellationToken);

        var redirectUriString = EntraAuthCodeDefaults.ResolveRedirectUri(connection.EntraAuthCodeRedirectUri);
        if (!EntraAuthCodeDefaults.TryValidateRedirectUri(redirectUriString, out var redirectUri) || redirectUri is null)
        {
            throw new InvalidOperationException("The configured Entra ID authorization-code redirect URI is not a valid loopback URI.");
        }

        CancellationTokenSource newCts;
        CancellationTokenSource? superseded;
        LoopbackAuthorizationCodeListener? supersededListener;
        lock (_gate)
        {
            superseded = _pendingCts;
            supersededListener = _pendingListener;
            newCts = new CancellationTokenSource();
            _pendingCts = newCts;
            _pendingListener = null;
        }

        if (superseded is not null)
        {
            _logger.LogInformation("Superseding an in-flight Entra ID authorization-code sign-in with a new attempt.");
            CancelPending(superseded);
            supersededListener?.Dispose();
        }

        var state = PkceGenerator.CreateState();
        var (codeVerifier, codeChallenge) = PkceGenerator.Create();
        var listener = StartListenerOrThrow(redirectUri, newCts);
        var authorizeUrl = BuildAuthorizeUrl(connection, redirectUriString, state, codeChallenge);
        var expiresAtUtc = _timeProvider.GetUtcNow().Add(EntraAuthCodeDefaults.CallbackTimeout);

        lock (_gate)
        {
            if (ReferenceEquals(_pendingCts, newCts))
            {
                _pendingListener = listener;
                _status = EntraAuthCodeSignInStatus.Pending(expiresAtUtc);
            }
        }

        _ = TrackCallbackAsync(listener, connection, redirectUriString, state, codeVerifier, newCts);

        return new EntraAuthCodeSignInHandle
        {
            AuthorizeUrl = authorizeUrl,
            ExpiresAtUtc = expiresAtUtc
        };
    }

    /// <inheritdoc />
    public EntraAuthCodeSignInStatus GetStatus()
    {
        lock (_gate)
        {
            return _status;
        }
    }

    public void Dispose()
    {
        CancellationTokenSource? pending;
        LoopbackAuthorizationCodeListener? pendingListener;
        lock (_gate)
        {
            pending = _pendingCts;
            pendingListener = _pendingListener;
            _pendingCts = null;
            _pendingListener = null;
        }

        if (pending is not null)
        {
            CancelPending(pending);
        }

        pendingListener?.Dispose();
    }

    private async Task TrackCallbackAsync(LoopbackAuthorizationCodeListener listener,
        StoredAzureFoundryConnection connection,
        string redirectUri,
        string expectedState,
        string codeVerifier,
        CancellationTokenSource cts)
    {
        try
        {
            var callback = await listener.WaitForCallbackAsync(expectedState, EntraAuthCodeDefaults.CallbackTimeout, cts.Token);

            switch (callback.Outcome)
            {
                case LoopbackCallbackOutcome.Success:
                    await CompleteRedemptionAsync(connection, callback.AuthorizationCode!, codeVerifier, redirectUri, cts);
                    break;

                case LoopbackCallbackOutcome.StateMismatch:
                    _logger.LogWarning("Entra ID authorization-code sign-in callback state did not match the expected value; rejecting the callback.");
                    UpdateStatusIfCurrent(cts, EntraAuthCodeSignInStatus.Failed);
                    break;

                case LoopbackCallbackOutcome.AadError:
                    _logger.LogWarning("Entra ID authorization-code sign-in was rejected: {Error} {ErrorDescription}",
                        callback.SanitizedError, callback.SanitizedErrorDescription);
                    UpdateStatusIfCurrent(cts, EntraAuthCodeSignInStatus.Failed);
                    break;

                case LoopbackCallbackOutcome.MissingCode:
                    _logger.LogWarning("Entra ID authorization-code sign-in callback carried no authorization code.");
                    UpdateStatusIfCurrent(cts, EntraAuthCodeSignInStatus.Failed);
                    break;

                default:
                    // LoopbackCallbackOutcome.TimedOut and any future outcome both resolve to Failed.
                    UpdateStatusIfCurrent(cts, EntraAuthCodeSignInStatus.Failed);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            UpdateStatusIfCurrent(cts, EntraAuthCodeSignInStatus.Failed);
        }
        catch (Exception exception) when (exception is MsalException or IOException or UnauthorizedAccessException)
        {
            // Never log token material; MSAL exception messages here describe the auth failure, not a token.
            _logger.LogWarning(exception, "Entra ID authorization-code sign-in did not complete successfully.");
            UpdateStatusIfCurrent(cts, EntraAuthCodeSignInStatus.Failed);
        }
        catch (Exception exception)
        {
            // StartAsync never awaits this, so an exception outside the specific catches above (a CryptographicException
            // from the account store's protector) would go unobserved AND strand the status at Pending; no token is logged.
            _logger.LogWarning(exception, "Entra ID authorization-code sign-in did not complete successfully.");
            UpdateStatusIfCurrent(cts, EntraAuthCodeSignInStatus.Failed);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_pendingCts, cts))
                {
                    _pendingCts = null;
                    _pendingListener = null;
                }
            }

            listener.Dispose();
            cts.Dispose();
        }
    }

    private async Task CompleteRedemptionAsync(StoredAzureFoundryConnection connection,
        string authorizationCode,
        string codeVerifier,
        string redirectUri,
        CancellationTokenSource cts)
    {
        var redemption = await _redeemer.RedeemAsync(connection, authorizationCode, codeVerifier, redirectUri, cts.Token);

        var cacheKey = EntraDeviceCodeCredentialCacheKey.Create(connection.EntraTenantId, connection.EntraClientId, connection.EntraTokenScope);
        var credential = new MsalDelegatedTokenCredential(redemption.ConfidentialClientApplication, redemption.Account, connection.EntraTokenScope!);
        _liveCredentialCache.Store(cacheKey, credential);

        // Persist with a fresh token: a superseded/cancelled attempt must not abort this save mid-flight.
        await _accountStore.SaveHomeAccountIdAsync(redemption.Account.HomeAccountId.Identifier, CancellationToken.None);

        if (UpdateStatusIfCurrent(cts, EntraAuthCodeSignInStatus.Succeeded))
        {
            try
            {
                _onSignInSucceeded?.Invoke();
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Entra ID post-sign-in selection-cache invalidation failed.");
            }
        }
    }

    /// <summary>Starts the loopback listener, translating a bound-port failure into the caller's exception shape.</summary>
    /// <remarks>
    ///     <c>HttpListener.Start()</c> throws when the redirect URI's port is already bound (a leftover attempt, or
    ///     another app on that loopback port), and <see cref="StartAsync" /> has already claimed the pending slot.
    ///     Uncaught, that slot dangles forever: no <c>TrackCallbackAsync</c> runs to clear <c>_pendingCts</c>, and the
    ///     exception misses the endpoint's <see cref="InvalidOperationException" /> catch, escaping as an unhandled 500
    ///     instead of a clean 400.
    /// </remarks>
    private LoopbackAuthorizationCodeListener StartListenerOrThrow(Uri redirectUri, CancellationTokenSource newCts)
    {
        try
        {
            return LoopbackAuthorizationCodeListener.Start(redirectUri);
        }
        catch (HttpListenerException exception)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_pendingCts, newCts))
                {
                    _pendingCts = null;
                }
            }

            newCts.Dispose();

            throw new InvalidOperationException("The Entra ID authorization-code sign-in port is busy — close the conflicting process or change the redirect URI port.",
                exception);
        }
    }

    private async Task<StoredAzureFoundryConnection> LoadConnectionOrThrowAsync(CancellationToken cancellationToken)
    {
        var config = await _credentialStore.LoadConfigAsync(cancellationToken);
        var connection = config?.AzureFoundry;
        if (connection is not { AuthMode: AzureFoundryAuthMode.EntraId, EntraSignInMethod: EntraSignInMethod.AuthorizationCode }
            || string.IsNullOrWhiteSpace(connection.EntraTenantId)
            || string.IsNullOrWhiteSpace(connection.EntraClientId)
            || string.IsNullOrWhiteSpace(connection.EntraClientSecret))
        {
            throw new EntraConnectionNotConfiguredException("No Entra ID connection configured for authorization-code sign-in (tenant id, client id, and client secret) is stored. " +
                                                            "Save Cloud Settings with auth mode EntraId and sign-in method AuthorizationCode first.");
        }

        return connection;
    }

    private static string BuildAuthorizeUrl(StoredAzureFoundryConnection connection, string redirectUri, string state, string codeChallenge)
    {
        var scope = $"{connection.EntraTokenScope} openid offline_access";
        var query = string.Join('&',
            $"client_id={Uri.EscapeDataString(connection.EntraClientId!)}",
            "response_type=code",
            $"redirect_uri={Uri.EscapeDataString(redirectUri)}",
            "response_mode=query",
            $"scope={Uri.EscapeDataString(scope)}",
            $"state={Uri.EscapeDataString(state)}",
            $"code_challenge={Uri.EscapeDataString(codeChallenge)}",
            "code_challenge_method=S256");

        return $"https://login.microsoftonline.com/{Uri.EscapeDataString(connection.EntraTenantId!)}/oauth2/v2.0/authorize?{query}";
    }

    private bool UpdateStatusIfCurrent(CancellationTokenSource cts, EntraAuthCodeSignInStatus status)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_pendingCts, cts))
            {
                return false;
            }

            _status = status;
            return true;
        }
    }

    private static void CancelPending(CancellationTokenSource cts)
    {
        try
        {
            // Forced sync: this helper is shared with Dispose(), which cannot await; the type is IDisposable and its
            // consumers do not "await using" it, so the disposal contract stays synchronous.
#pragma warning disable MA0045 // forced sync: shared with IDisposable.Dispose (see comment above)
            cts.Cancel();
#pragma warning restore MA0045
        }
        catch (ObjectDisposedException)
        {
            // Already completed and disposed by its own tracking task; nothing to cancel.
        }
    }
}

namespace XE_Local_AI_Engine.Client.Services.CloudProviders.Auth;

using Azure.Identity;

/// <summary>
///     Owns the pending Entra ID device-code sign-in lifecycle so the Operator endpoints can start a device-code
///     flow, return the user code + verification URL immediately, and poll status until it completes. A second
///     <see cref="StartAsync" /> <em>supersedes</em> any in-flight attempt. Mirrors <c>CodexLoginCoordinator</c>'s
///     pending-login shape. Never logs token material.
/// </summary>
public sealed class EntraDeviceCodeSignInCoordinator : IEntraDeviceCodeSignInCoordinator, IDisposable
{
    private const string TokenCachePersistenceName = "XE-Local-AI-Engine.Client.AzureFoundry.EntraId";

    private readonly ICloudCredentialStore _credentialStore;
    private readonly IEntraLiveCredentialCache _liveCredentialCache;
    private readonly Lock _gate = new();
    private readonly ILogger<EntraDeviceCodeSignInCoordinator> _logger;
    private readonly Action? _onSignInSucceeded;
    private readonly IEntraTokenCacheStore _tokenCacheStore;

    private CancellationTokenSource? _pendingCts;
    private EntraDeviceCodeSignInStatus _status = EntraDeviceCodeSignInStatus.None;

    /// <param name="credentialStore">Reads the stored Azure Foundry connection's tenant / client / scope.</param>
    /// <param name="tokenCacheStore">Persists the authentication record on success.</param>
    /// <param name="liveCredentialCache">
    ///     Keeps the successfully-authenticated credential instance alive for the process lifetime so the chat-client
    ///     factory reuses it — its MSAL token cache is what actually holds the refresh token, which a credential
    ///     rebuilt later from only the persisted record has no access to when OS-native persistence is unavailable.
    /// </param>
    /// <param name="logger">Never receives token material.</param>
    /// <param name="onSignInSucceeded">
    ///     Optional callback invoked once a sign-in completes and a record is persisted. The host wires this to
    ///     invalidate the active-cloud selection snapshot so a sign-in takes effect on the very next send.
    /// </param>
    public EntraDeviceCodeSignInCoordinator(ICloudCredentialStore credentialStore,
        IEntraTokenCacheStore tokenCacheStore,
        IEntraLiveCredentialCache liveCredentialCache,
        ILogger<EntraDeviceCodeSignInCoordinator> logger,
        Action? onSignInSucceeded = null)
    {
        ArgumentNullException.ThrowIfNull(credentialStore);
        ArgumentNullException.ThrowIfNull(tokenCacheStore);
        ArgumentNullException.ThrowIfNull(liveCredentialCache);
        ArgumentNullException.ThrowIfNull(logger);

        _credentialStore = credentialStore;
        _tokenCacheStore = tokenCacheStore;
        _liveCredentialCache = liveCredentialCache;
        _logger = logger;
        _onSignInSucceeded = onSignInSucceeded;
    }

    /// <inheritdoc />
    public async Task<EntraDeviceCodeSignInHandle> StartAsync(CancellationToken cancellationToken)
    {
        var connection = await LoadEntraConnectionOrThrowAsync(cancellationToken).ConfigureAwait(false);

        CancellationTokenSource newCts;
        CancellationTokenSource? superseded;
        lock (_gate)
        {
            superseded = _pendingCts;
            newCts = new CancellationTokenSource();
            _pendingCts = newCts;
        }

        if (superseded is not null)
        {
            _logger.LogInformation("Superseding an in-flight Entra ID device-code sign-in with a new attempt.");
            CancelPending(superseded);
        }

        var (deviceCodeInfo, credential, completion) = await BeginDeviceCodeFlowAsync(connection, allowPersistence: true, newCts.Token).ConfigureAwait(false);

        lock (_gate)
        {
            if (ReferenceEquals(_pendingCts, newCts))
            {
                _status = EntraDeviceCodeSignInStatus.Pending(deviceCodeInfo.UserCode, deviceCodeInfo.VerificationUri.ToString(), deviceCodeInfo.ExpiresOn);
            }
        }

        var cacheKey = EntraDeviceCodeCredentialCacheKey.Create(connection.EntraTenantId, connection.EntraClientId, connection.EntraTokenScope);
        _ = TrackCompletionAsync(completion, credential, cacheKey, newCts);

        return new EntraDeviceCodeSignInHandle(deviceCodeInfo.UserCode, deviceCodeInfo.VerificationUri.ToString(), deviceCodeInfo.ExpiresOn);
    }

    /// <inheritdoc />
    public EntraDeviceCodeSignInStatus GetStatus()
    {
        lock (_gate)
        {
            return _status;
        }
    }

    public void Dispose()
    {
        CancellationTokenSource? pending;
        lock (_gate)
        {
            pending = _pendingCts;
            _pendingCts = null;
        }

        if (pending is not null)
        {
            CancelPending(pending);
        }
    }

    // Requests the initial device code and races it against the background AuthenticateAsync task: if the platform's
    // encrypted token-cache persistence is unavailable, that surfaces as CredentialUnavailableException before (or
    // instead of) the device-code callback firing, in which case a single retry rebuilds the credential without
    // persistence (in-memory only, logged) — never unencrypted-on-disk.
    private async Task<(DeviceCodeInfo Info, DeviceCodeCredential Credential, Task<AuthenticationRecord> Completion)> BeginDeviceCodeFlowAsync(StoredAzureFoundryConnection connection,
        bool allowPersistence,
        CancellationToken cancellationToken)
    {
        var deviceCodeReady = new TaskCompletionSource<DeviceCodeInfo>(TaskCreationOptions.RunContinuationsAsynchronously);

        var credential = new DeviceCodeCredential(new DeviceCodeCredentialOptions
        {
            TenantId = connection.EntraTenantId,
            ClientId = connection.EntraClientId,
            TokenCachePersistenceOptions = allowPersistence
                ? new TokenCachePersistenceOptions
                {
                    Name = TokenCachePersistenceName
                }
                : null,
            DeviceCodeCallback = (info, _) =>
            {
                deviceCodeReady.TrySetResult(info);
                return Task.CompletedTask;
            }
        });

        var authenticateTask = credential.AuthenticateAsync(cancellationToken);

        // Propagate a fault (e.g. bad tenant/client, or persistence unavailable before the callback ever fired) to
        // the awaiter below instead of leaving it to hang forever.
        _ = authenticateTask.ContinueWith(task => deviceCodeReady.TrySetException(task.Exception!.GetBaseException()),
            cancellationToken,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        try
        {
            var info = await deviceCodeReady.Task.ConfigureAwait(false);
            return (info, credential, authenticateTask);
        }
        // A persistence failure does not always surface as CredentialUnavailableException — on a platform with no
        // org.freedesktop.secrets provider (e.g. WSL2 without gnome-keyring/kwallet) it can arrive as
        // AuthenticationFailedException wrapping MsalCachePersistenceException several levels deep instead (live-
        // confirmed on WSL2; see EntraCachePersistenceFailure's remarks). Checking both is what makes the retry
        // actually fire instead of the failure escaping as an unhandled error from the sign-in endpoint.
        catch (Exception exception) when (allowPersistence && (exception is CredentialUnavailableException || EntraCachePersistenceFailure.IsPersistenceUnavailable(exception)))
        {
            _logger.LogWarning(exception, "Encrypted Entra ID token-cache persistence is unavailable on this platform; retrying device-code sign-in with an in-memory (non-persisted) token cache.");
            return await BeginDeviceCodeFlowAsync(connection, allowPersistence: false, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task TrackCompletionAsync(Task<AuthenticationRecord> completion, DeviceCodeCredential credential, string cacheKey, CancellationTokenSource cts)
    {
        try
        {
            var record = await completion.ConfigureAwait(false);

            // Keep the live, already-authenticated credential alive for the chat-client factory to reuse: its MSAL
            // token cache (in-memory always, plus OS-native encrypted disk when available) is what actually holds
            // the refresh token — a credential rebuilt later from only the persisted record has nothing to silently
            // refresh from when encrypted persistence is unavailable on this platform.
            _liveCredentialCache.Store(cacheKey, credential);

            // Persist with a fresh token: a superseded/cancelled attempt must not abort this save mid-flight.
            await _tokenCacheStore.SaveRecordAsync(record, CancellationToken.None).ConfigureAwait(false);

            if (UpdateStatusIfCurrent(cts, EntraDeviceCodeSignInStatus.Succeeded))
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
        catch (OperationCanceledException)
        {
            UpdateStatusIfCurrent(cts, EntraDeviceCodeSignInStatus.Failed);
        }
        catch (Exception exception) when (exception is CredentialUnavailableException or AuthenticationFailedException or IOException or UnauthorizedAccessException)
        {
            // Never log token material; Azure.Identity exception messages here describe the auth failure, not a token.
            _logger.LogWarning(exception, "Entra ID device-code sign-in did not complete successfully.");
            UpdateStatusIfCurrent(cts, EntraDeviceCodeSignInStatus.Failed);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_pendingCts, cts))
                {
                    _pendingCts = null;
                }
            }

            cts.Dispose();
        }
    }

    private async Task<StoredAzureFoundryConnection> LoadEntraConnectionOrThrowAsync(CancellationToken cancellationToken)
    {
        var config = await _credentialStore.LoadConfigAsync(cancellationToken).ConfigureAwait(false);
        var connection = config?.AzureFoundry;
        if (connection is not { AuthMode: AzureFoundryAuthMode.EntraId }
            || string.IsNullOrWhiteSpace(connection.EntraTenantId)
            || string.IsNullOrWhiteSpace(connection.EntraClientId))
        {
            throw new EntraConnectionNotConfiguredException("No Entra ID connection with a tenant id and client id is stored. Save Cloud Settings with auth mode EntraId first.");
        }

        return connection;
    }

    private bool UpdateStatusIfCurrent(CancellationTokenSource cts, EntraDeviceCodeSignInStatus status)
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
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already completed and disposed by its own tracking task; nothing to cancel.
        }
    }
}

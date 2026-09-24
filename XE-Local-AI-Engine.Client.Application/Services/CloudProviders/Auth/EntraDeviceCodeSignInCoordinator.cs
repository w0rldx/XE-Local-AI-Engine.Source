namespace XE_Local_AI_Engine.Client.Services.CloudProviders.Auth;

using Azure.Identity;

/// <summary>
///     Owns the pending Entra ID device-code sign-in lifecycle so the Operator endpoints can start a device-code
///     flow, return the user code and verification URL immediately, and poll status until it completes.
/// </summary>
/// <remarks>
///     A second <see cref="StartAsync" /> <em>supersedes</em> any in-flight attempt, and token material is never
///     logged. The authenticated credential is kept alive for the process lifetime because its MSAL token cache is
///     what holds the refresh token: a credential rebuilt later from only the persisted record has nothing to
///     silently refresh from when OS-native encrypted persistence is unavailable. Mirrors
///     <c>CodexLoginCoordinator</c>'s pending-login shape.
/// </remarks>
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
    ///     Keeps the authenticated credential alive for the process lifetime so the chat-client factory reuses it; the
    ///     reason is in this type's remarks.
    /// </param>
    /// <param name="logger">Never receives token material.</param>
    /// <param name="onSignInSucceeded">
    ///     Optional; runs once a record is persisted. The host wires it to invalidate the active-cloud snapshot, so a
    ///     sign-in takes effect on the next send.
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
        var connection = await LoadEntraConnectionOrThrowAsync(cancellationToken);

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

        var (deviceCodeInfo, credential, completion) = await BeginDeviceCodeFlowAsync(connection, allowPersistence: true, newCts.Token);

        lock (_gate)
        {
            if (ReferenceEquals(_pendingCts, newCts))
            {
                _status = EntraDeviceCodeSignInStatus.Pending(deviceCodeInfo.UserCode, deviceCodeInfo.VerificationUri.ToString(), deviceCodeInfo.ExpiresOn);
            }
        }

        var cacheKey = EntraDeviceCodeCredentialCacheKey.Create(connection.EntraTenantId, connection.EntraClientId, connection.EntraTokenScope);
        _ = TrackCompletionAsync(completion, credential, cacheKey, newCts);

        return new EntraDeviceCodeSignInHandle
        {
            UserCode = deviceCodeInfo.UserCode,
            VerificationUri = deviceCodeInfo.VerificationUri.ToString(),
            ExpiresAtUtc = deviceCodeInfo.ExpiresOn
        };
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

    /// <summary>Requests the initial device code, racing it against the background <c>AuthenticateAsync</c> task.</summary>
    /// <remarks>
    ///     When the platform's encrypted token-cache persistence is unavailable that surfaces as
    ///     <see cref="CredentialUnavailableException" /> before (or instead of) the device-code callback firing; a
    ///     single retry then rebuilds the credential without persistence — in-memory only and logged, never
    ///     unencrypted on disk.
    /// </remarks>
    private async Task<DeviceCodeFlowHandle> BeginDeviceCodeFlowAsync(StoredAzureFoundryConnection connection,
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
            var info = await deviceCodeReady.Task;
            return new DeviceCodeFlowHandle(info, credential, authenticateTask);
        }
        // A persistence failure does not always surface as CredentialUnavailableException: with no org.freedesktop.secrets provider it can arrive as
        // AuthenticationFailedException wrapping MsalCachePersistenceException several levels deep (see EntraCachePersistenceFailure). Checking both is what makes this retry fire at all.
        catch (Exception exception) when (allowPersistence && (exception is CredentialUnavailableException || EntraCachePersistenceFailure.IsPersistenceUnavailable(exception)))
        {
            _logger.LogWarning(exception, "Encrypted Entra ID token-cache persistence is unavailable on this platform; retrying device-code sign-in with an in-memory (non-persisted) token cache.");
            return await BeginDeviceCodeFlowAsync(connection, allowPersistence: false, cancellationToken);
        }
    }

    private async Task TrackCompletionAsync(Task<AuthenticationRecord> completion, DeviceCodeCredential credential, string cacheKey, CancellationTokenSource cts)
    {
        try
        {
            var record = await completion;

            // Keep the live, already-authenticated credential alive for the chat-client factory to reuse: its MSAL token
            // cache (in-memory always, OS-native encrypted disk when available) is what holds the refresh token.
            _liveCredentialCache.Store(cacheKey, credential);

            // Persist with a fresh token: a superseded/cancelled attempt must not abort this save mid-flight.
            await _tokenCacheStore.SaveRecordAsync(record, CancellationToken.None);

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
        var config = await _credentialStore.LoadConfigAsync(cancellationToken);
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

    // An initiated device-code flow: the code to show the operator, the credential that must stay alive to hold the
    // MSAL token cache, and the still-running authentication whose completion carries the record to persist.
    private sealed record DeviceCodeFlowHandle(DeviceCodeInfo Info, DeviceCodeCredential Credential, Task<AuthenticationRecord> Completion);
}

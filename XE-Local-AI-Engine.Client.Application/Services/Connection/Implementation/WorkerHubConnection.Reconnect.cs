namespace XE_Local_AI_Engine.Client.Services.Connection.Implementation;

using NSec.Cryptography;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Services.Auth;

public sealed partial class WorkerHubConnection
{
    private async Task RegisterNodeKeyAsync(CancellationToken cancellationToken = default)
    {
        var keyId = Guid.NewGuid();
#pragma warning disable CA2000 // NodeKeyRegistry takes ownership and disposes the key
        var privateKey = Key.Create(KeyAgreementAlgorithm.X25519, new KeyCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport
        });
#pragma warning restore CA2000
        _nodeKeyRegistry.Rotate(keyId.ToString("N"), privateKey);

        var publicKeyBytes = _nodeKeyRegistry.ActivePublicKey.Export(KeyBlobFormat.RawPublicKey);
        var publicKeyBase64 = Convert.ToBase64String(publicKeyBytes);
        var popChallenge = keyId.ToString("N");
        var popSignature = publicKeyBase64;

        await SendWorkerKeyRegisteredAsync(keyId, publicKeyBase64, popSignature, popChallenge, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> GetRequiredAccessTokenAsync()
    {
        if (!await EnsureFreshAccessTokenAsync().ConfigureAwait(false))
        {
            throw new InvalidOperationException("No valid access token available. Re-pairing is required.");
        }

        var token = await _tokenStore.GetAccessTokenAsync().ConfigureAwait(false);
        return !string.IsNullOrWhiteSpace(token)
            ? token
            : throw new InvalidOperationException("No access token available. Re-pairing is required.");
    }

    private async Task<bool> EnsureFreshAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        if (!await ShouldRefreshAccessTokenAsync().ConfigureAwait(false))
        {
            return true;
        }

        await _tokenRefreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!await ShouldRefreshAccessTokenAsync().ConfigureAwait(false))
            {
                return true;
            }

            _logger.LogInformation("Worker access token is expired or close to expiry. Attempting refresh before hub authentication.");
            var outcome = await _workerTokenRefreshService.TryRefreshAsync(cancellationToken).ConfigureAwait(false);
            if (outcome == WorkerTokenRefreshOutcome.CredentialsRevoked)
            {
                throw new WorkerCredentialsRevokedException();
            }

            if (outcome == WorkerTokenRefreshOutcome.TransientFailure)
            {
                return false;
            }

            var token = await _tokenStore.GetAccessTokenAsync().ConfigureAwait(false);
            return !string.IsNullOrWhiteSpace(token);
        }
        finally
        {
            _tokenRefreshLock.Release();
        }
    }

    private async Task<bool> ShouldRefreshAccessTokenAsync()
    {
        var token = await _tokenStore.GetAccessTokenAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
        {
            return true;
        }

        return _tokenStore.TokenExpiresAt is not { } expiresAt || expiresAt <= _timeProvider.GetUtcNow().Add(AccessTokenRefreshSkew);
    }

    private static bool IsInactiveHubSendException(Exception exception)
    {
        return exception is InvalidOperationException invalidOperationException &&
               invalidOperationException.Message.StartsWith("Worker hub connection is not active.", StringComparison.Ordinal);
    }

    private async Task DisposeHubConnectionAsync()
    {
        if (_hubConnection is null)
        {
            return;
        }

        var connection = _hubConnection;
        _hubConnection = null;

        connection.Closed -= OnConnectionClosedAsync;
        connection.Reconnecting -= OnReconnectingAsync;
        connection.Reconnected -= OnReconnectedAsync;

        await connection.DisposeAsync().ConfigureAwait(false);
    }

    private Task OnConnectionClosedAsync(Exception? exception)
    {
        // SignalR discards the real RetryReason when reconnect retries are exhausted: the Closed event
        // surfaces a synthetic "retries exhausted" OperationCanceledException, NOT the
        // WorkerCredentialsRevokedException that actually stopped the loop. We therefore detect revocation
        // two ways: (1) the latch set by the reconnect policy when it returned null for a revoked reason,
        // and (2) the exception chain, which still carries the typed exception on the initial-connect path.
        if (Volatile.Read(ref _credentialsRevoked) || ContainsCredentialsRevoked(exception))
        {
            _connectionState.TransitionTo(WorkerConnectionState.Error, "Worker credentials could not be refreshed. Re-pairing is required.");
            return Task.CompletedTask;
        }

        _connectionState.TransitionTo(WorkerConnectionState.Disconnected, exception?.Message);
        return Task.CompletedTask;
    }

    private void OnReconnectPolicyDetectedRevokedCredentials()
    {
        Volatile.Write(ref _credentialsRevoked, true);
    }

    private static bool ContainsCredentialsRevoked(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is WorkerCredentialsRevokedException)
            {
                return true;
            }
        }

        return false;
    }

    private Task OnReconnectingAsync(Exception? exception)
    {
        _connectionState.TransitionTo(WorkerConnectionState.Reconnecting, exception?.Message);
        return Task.CompletedTask;
    }

    private async Task OnReconnectedAsync(string? connectionId)
    {
        _logger.LogInformation("Worker hub connection reconnected with connection id {ConnectionId}.", connectionId);

        bool tokenIsFresh;
        try
        {
            tokenIsFresh = await EnsureFreshAccessTokenAsync().ConfigureAwait(false);
        }
        catch (WorkerCredentialsRevokedException exception)
        {
            _logger.LogWarning(exception, "Worker credentials were revoked during reconnect. Re-pairing is required.");
            _connectionState.TransitionTo(WorkerConnectionState.Error, "Worker credentials could not be refreshed. Re-pairing is required.");
            return;
        }

        if (!tokenIsFresh)
        {
            _connectionState.TransitionTo(WorkerConnectionState.Error, "Worker credentials could not be refreshed. Re-pairing is required.");
            return;
        }

        var clientNodeId = await _tokenStore.GetClientNodeIdAsync().ConfigureAwait(false);
        if (clientNodeId is not null)
        {
            await SendWorkerHelloAsync(clientNodeId.Value).ConfigureAwait(false);
            await _capabilityReporter.Value.ReportToApiAsync().ConfigureAwait(false);
            await RegisterNodeKeyAsync().ConfigureAwait(false);
            await _deadLetterFlushService.FlushAsync().ConfigureAwait(false);
        }

        _connectionState.TransitionTo(WorkerConnectionState.Connected);
    }

    private void OnConnectionStateChanged(object? sender, WorkerConnectionStateChangedEventArgs eventArgs)
    {
        StateChanged?.Invoke(this, eventArgs);
    }

    private static string BuildHubUrl(CentralPlatformOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var baseUri = new Uri(options.BaseUrl, UriKind.Absolute);
        return new Uri(baseUri, options.HubPath).ToString();
    }

    private sealed class NoOpWorkerTokenRefreshService : IWorkerTokenRefreshService
    {
        public static readonly NoOpWorkerTokenRefreshService Instance = new();

        private NoOpWorkerTokenRefreshService()
        {
        }

        public Task<WorkerTokenRefreshOutcome> TryRefreshAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(WorkerTokenRefreshOutcome.TransientFailure);
        }
    }
}

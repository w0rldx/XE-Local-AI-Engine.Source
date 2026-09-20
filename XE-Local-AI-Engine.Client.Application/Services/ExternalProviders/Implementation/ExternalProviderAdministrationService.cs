namespace XE_Local_AI_Engine.Client.Services.ExternalProviders.Implementation;

using XE_Local_AI_Engine.Client.Services.CloudProviders;

/// <summary>
///     Default <see cref="IExternalProviderAdministrationService" />: encrypted store first, then the derived state,
///     then the caches.
/// </summary>
/// <remarks>
///     The ORDER is the design: the encrypted file is the source of truth and commits first, so everything else is
///     repairable from it; then BOTH caches are dropped, before anything fallible runs; and only then does the
///     reconciliation pass write the provider map and the allow-list. The caches are cleared on EVERY committed change,
///     not only when reconciliation repaired something. Why that ordering and not a <c>finally</c>:
///     docs/wiki/03-local-runtime-and-providers.md, "External connections: the store, the registry cache, and the reconciler".
/// </remarks>
public sealed class ExternalProviderAdministrationService : IExternalProviderAdministrationService
{
    private readonly ILocalChatClientCacheInvalidator _chatClientCacheInvalidator;
    private readonly ILogger<ExternalProviderAdministrationService> _logger;
    private readonly IExternalProviderReconciler _reconciler;
    private readonly IExternalProviderRegistryCache _registryCache;
    private readonly IExternalProviderStore _store;

    public ExternalProviderAdministrationService(IExternalProviderStore store,
        IExternalProviderRegistryCache registryCache,
        IExternalProviderReconciler reconciler,
        ILocalChatClientCacheInvalidator chatClientCacheInvalidator,
        ILogger<ExternalProviderAdministrationService> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _registryCache = registryCache ?? throw new ArgumentNullException(nameof(registryCache));
        _reconciler = reconciler ?? throw new ArgumentNullException(nameof(reconciler));
        _chatClientCacheInvalidator = chatClientCacheInvalidator ?? throw new ArgumentNullException(nameof(chatClientCacheInvalidator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<ExternalProviderWriteResult> SaveConnectionAsync(ExternalProviderConnectionSaveRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await _store.SaveConnectionAsync(request, cancellationToken);
        if (result is ExternalProviderWriteResult.Committed committed)
        {
            await ApplySideEffectsAsync(committed.Changed, cancellationToken);
            _logger.LogInformation("Saved external connection '{ConnectionId}' with {ModelCount} registered model(s).",
                request.Id,
                request.Models.Count);
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<ExternalProviderWriteResult> DeleteConnectionAsync(string connectionId,
        string? expectedRevision,
        CancellationToken cancellationToken = default)
    {
        var result = await _store.DeleteConnectionAsync(connectionId, expectedRevision, cancellationToken);
        if (result is ExternalProviderWriteResult.Committed committed)
        {
            await ApplySideEffectsAsync(committed.Changed, cancellationToken);
            if (committed.Changed)
            {
                _logger.LogInformation("Deleted external connection '{ConnectionId}' and its registered models.", connectionId);
            }
        }

        return result;
    }

    private async Task ApplySideEffectsAsync(bool changed, CancellationToken cancellationToken)
    {
        // Always invalidated, even on a no-op save: the cheapest correct thing is one re-projection of a file the node
        // just read, and the alternative — reasoning about which no-ops are truly no-ops — is how a stale generation survives an edit.
        _registryCache.Invalidate();

        // BEFORE reconciliation, not after it: reconciliation is fallible and can be slow (a lease timeout, a locked settings
        // file, a cancelled request), and every moment it runs the router would otherwise keep serving chat clients built against the previous key.
        if (changed)
        {
            _chatClientCacheInvalidator.ClearClientCache();
        }

        _ = await _reconciler.ReconcileAsync(cancellationToken);
    }
}

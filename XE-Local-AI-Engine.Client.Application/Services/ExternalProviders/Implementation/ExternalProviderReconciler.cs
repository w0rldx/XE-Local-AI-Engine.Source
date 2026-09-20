namespace XE_Local_AI_Engine.Client.Services.ExternalProviders.Implementation;

using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.Abstractions.External;
using XE_Local_AI_Engine.Providers.OpenAICompat;

/// <summary>
///     Default <see cref="IExternalProviderReconciler" />. Diffs the registry against the <c>ext:</c>-prefixed
///     provider-map rows, the <c>ext:</c>-prefixed tool-capable allow-list entries, and the node default model, and
///     repairs each difference.
/// </summary>
/// <remarks>
///     Only <c>ext:</c>-scheme keys are ever touched, which is what makes the pass safe to run unconditionally at
///     startup. Two comparison rules are load-bearing and deliberately different, each applied CONSISTENTLY on both
///     halves of its own diff: the provider map is case-INSENSITIVE, the tool-capable allow-list is matched ORDINALLY
///     (<c>LocalToolOfferProvider.IsToolCapable</c> compares ordinally). Both sides are fed the ONE canonical spelling
///     the store minted. Why each: docs/wiki/03-local-runtime-and-providers.md, "External connections: the store, the registry cache, and the reconciler".
/// </remarks>
public sealed class ExternalProviderReconciler : IExternalProviderReconciler
{
    private readonly ILocalChatClientCacheInvalidator _chatClientCacheInvalidator;
    private readonly IModelProviderMapLeaseCoordinator _leaseCoordinator;
    private readonly ILogger<ExternalProviderReconciler> _logger;
    private readonly ICoordinatedModelProviderMapStore _mapStore;
    private readonly ILocalModelProviderResolver _providerResolver;
    private readonly INodeSettingsStore _settingsStore;
    private readonly IExternalProviderStore _store;

    public ExternalProviderReconciler(IExternalProviderStore store,
        ICoordinatedModelProviderMapStore mapStore,
        IModelProviderMapLeaseCoordinator leaseCoordinator,
        ILocalModelProviderResolver providerResolver,
        ILocalChatClientCacheInvalidator chatClientCacheInvalidator,
        INodeSettingsStore settingsStore,
        ILogger<ExternalProviderReconciler> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _mapStore = mapStore ?? throw new ArgumentNullException(nameof(mapStore));
        _leaseCoordinator = leaseCoordinator ?? throw new ArgumentNullException(nameof(leaseCoordinator));
        _providerResolver = providerResolver ?? throw new ArgumentNullException(nameof(providerResolver));
        _chatClientCacheInvalidator = chatClientCacheInvalidator ?? throw new ArgumentNullException(nameof(chatClientCacheInvalidator));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<ExternalProviderReconciliationReport> ReconcileAsync(CancellationToken cancellationToken = default)
    {
        // Read the STORE's state, not just its contents. This pass DELETES every ext: map row, allow-list entry and node default the
        // configuration does not list, so a non-authoritative read repairs nothing: a config that merely looks empty would erase the operator's whole setup.
        if (await _store.ReadForWriteAsync(cancellationToken) is not ExternalProviderLoadResult.Loaded loaded)
        {
            _logger.LogWarning("Skipping external provider reconciliation: the connection store is not readable. Nothing was changed.");
            return new ExternalProviderReconciliationReport(0, 0, 0, 0, DefaultModelCleared: false);
        }

        // Project the configuration THIS pass loaded, never the registry: the registry re-reads the store through LoadAsync, which collapses an
        // Unreadable or unsupported-schema file to an EMPTY configuration — and an empty registration set is a mandate to erase everything.
        var registrations = ExternalProviderConfigProjection.Project(loaded.Config).Registrations;

        // Ordinal: these ids came out of the registry, which is keyed by the canonical spelling the store minted.
        var registered = registrations.Select(registration => registration.ModelId).ToArray();
        var registeredSet = new HashSet<string>(registered, StringComparer.Ordinal);

        var (written, removed) = await ReconcileProviderMapAsync(registered, registeredSet, cancellationToken);
        var (added, dropped, defaultCleared) = await ReconcileNodeSettingsAsync(registrations, registeredSet, cancellationToken);

        var report = new ExternalProviderReconciliationReport(written, removed, added, dropped, defaultCleared);
        if (report.Changed)
        {
            // The resolver memoizes model→provider for a few seconds and the router caches a chat client per (provider, model),
            // so a repaired row neither of them sees is a repair that has not taken effect until both caches expire.
            _providerResolver.InvalidateModelProviderMap();
            _chatClientCacheInvalidator.ClearClientCache();
            _logger.LogInformation(
                "External provider reconciliation repaired drift: {MapWritten} map row(s) written, {MapRemoved} removed, {AllowAdded} allow-list entr(ies) added, {AllowRemoved} removed, default cleared: {DefaultCleared}.",
                report.MapRowsWritten,
                report.MapRowsRemoved,
                report.AllowListAdded,
                report.AllowListRemoved,
                report.DefaultModelCleared);
        }

        return report;
    }

    private async Task<(int Written, int Removed)> ReconcileProviderMapAsync(IReadOnlyList<string> registered,
        HashSet<string> registeredIds,
        CancellationToken cancellationToken)
    {
        // Snapshot first, repair per row under its own lease. See ICoordinatedModelProviderMapStore.ListAsync for why
        // the listing is deliberately lease-free and what re-reads under the lease.
        var rows = await _mapStore.ListAsync(cancellationToken);
        var externalRows = rows.Where(row => ExternalModelId.HasExternalScheme(row.ModelName)).ToArray();

        var written = 0;
        foreach (var modelId in registered)
        {
            // Case-INSENSITIVE row match: the map key is NOCASE, so a row spelled with a different case IS this
            // model's row and must be repaired rather than duplicated.
            var row = externalRows.FirstOrDefault(candidate => string.Equals(candidate.ModelName, modelId, StringComparison.OrdinalIgnoreCase));
            if (row is not null && string.Equals(row.ProviderName, ExternalProviderConstants.ProviderName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (await TryWriteMapRowAsync(modelId, cancellationToken))
            {
                written++;
            }
        }

        // Orphan detection asks the SAME question the coverage loop above asks, spelled the same way: the map key is NOCASE, so a row is an
        // orphan only when NO registered id matches it case-insensitively. Asking ordinally here is what deleted `ext:conn/Foo`'s shared SQLite row.
        var registeredRowKeys = new HashSet<string>(registeredIds, StringComparer.OrdinalIgnoreCase);
        var orphans = externalRows.Select(row => row.ModelName)
                                  .Where(modelName => ExternalModelId.Canonicalize(modelName) is not { } canonical
                                                      || !registeredRowKeys.Contains(canonical));

        var removed = 0;
        foreach (var modelName in orphans)
        {
            if (await TryRemoveMapRowAsync(modelName, cancellationToken))
            {
                removed++;
            }
        }

        return (written, removed);
    }

    private async Task<bool> TryWriteMapRowAsync(string modelId, CancellationToken cancellationToken)
    {
        try
        {
            await using var lease = await _leaseCoordinator.AcquireMapMutationAsync(modelId,
                ModelProviderMapMutationKind.MapUpsert,
                cancellationToken);

            // Re-read UNDER the lease: the snapshot that selected this model may be stale, and the upsert needs the
            // row's current revision to compare-and-swap against.
            var current = await _mapStore.ReadWithRevisionAsync(lease, modelId, cancellationToken);
            if (current is not null && string.Equals(current.ProviderName, ExternalProviderConstants.ProviderName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (current is not null)
            {
                // A non-external provider already owns this exact name. Refuse rather than steal it: the name grammar makes this
                // practically impossible (no GGUF or Ollama model is called "ext:…"), so reaching here means something is genuinely wrong.
                _logger.LogWarning("The provider-map row for external model '{ModelId}' is owned by provider '{ProviderName}'; leaving it untouched.",
                    modelId,
                    current.ProviderName);
                return false;
            }

            var result = await _mapStore.TryUpsertAsync(lease,
                modelId,
                ExternalProviderConstants.ProviderName,
                expectedRevision: null,
                cancellationToken);
            return result is ProviderMapMutationResult.Mutated;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            // One model's repair failing must not abandon the rest: the pass is idempotent and runs again on the next
            // save or boot, so the honest response is to log and continue.
            _logger.LogWarning(exception, "Could not write the provider-map row for external model '{ModelId}'; the next reconciliation pass retries it.", modelId);
            return false;
        }
    }

    private async Task<bool> TryRemoveMapRowAsync(string modelName, CancellationToken cancellationToken)
    {
        try
        {
            await using var lease = await _leaseCoordinator.AcquireMapMutationAsync(modelName,
                ModelProviderMapMutationKind.MapRemove,
                cancellationToken);

            var current = await _mapStore.ReadWithRevisionAsync(lease, modelName, cancellationToken);
            if (current is null || !string.Equals(current.ProviderName, ExternalProviderConstants.ProviderName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var result = await _mapStore.TryRemoveIfMatchAsync(lease,
                current.ModelName,
                current.ProviderName,
                current.Revision,
                cancellationToken);
            return result is ProviderMapRemovalResult.Removed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            _logger.LogWarning(exception, "Could not remove the orphaned provider-map row for '{ModelName}'; the next reconciliation pass retries it.", modelName);
            return false;
        }
    }

    private async Task<(int Added, int Removed, bool DefaultCleared)> ReconcileNodeSettingsAsync(IReadOnlyList<ExternalProviderModelRegistration> registrations,
        HashSet<string> registeredSet,
        CancellationToken cancellationToken)
    {
        var desired = registrations.Where(registration => registration.Model.SupportsTools)
                                   .Select(registration => registration.ModelId)
                                   .Distinct(StringComparer.Ordinal)
                                   .ToArray();

        // The cheap pre-check reads through the settings cache ONLY to skip the write on the common no-drift pass. The authoritative
        // decision is re-made under the lock below, so a stale read here can cost a redundant write but never a wrong one.
        var preview = await _settingsStore.LoadAsync(cancellationToken);
        if (Diff(preview, desired, registeredSet) is (0, 0, false))
        {
            return (0, 0, false);
        }

        var added = 0;
        var removed = 0;
        var defaultCleared = false;
        string? clearedDefault = null;

        // Coordinated: load and save under ONE lock, because node-settings is written WHOLE — a read-compute-save-back shape
        // silently reverts an operator's unrelated edit that lands in the window. The mutation is pure and allocation-only, as the contract requires.
        _ = await _settingsStore.UpdateAsync(stored =>
        {
            (added, removed, defaultCleared) = Diff(stored, desired, registeredSet);
            clearedDefault = defaultCleared ? stored.DefaultModelName : null;

            var existing = stored.ToolCapableModels ?? [];

            // Every non-external entry keeps its exact spelling AND its position: the allow-list is operator-curated,
            // and an auto-sync that reordered or re-cased a hand-added local model would look like data loss.
            var merged = new List<string>(existing.Count + desired.Length);
            merged.AddRange(existing.Where(entry => !ExternalModelId.HasExternalScheme(entry)));
            merged.AddRange(desired);

            return stored with
            {
                ToolCapableModels = merged,
                DefaultModelName = defaultCleared ? null : stored.DefaultModelName
            };
        }, cancellationToken);

        if (defaultCleared)
        {
            _logger.LogInformation("Cleared the node default model: '{ModelName}' is an external model that is no longer registered.", clearedDefault);
        }

        return (added, removed, defaultCleared);
    }

    /// <summary>
    ///     What this pass would change about <paramref name="stored" />: how many <c>ext:</c> allow-list entries appear,
    ///     how many disappear, and whether the node default is a dead external selection.
    /// </summary>
    /// <remarks>
    ///     A <c>DefaultModelName</c> that is an <c>ext:</c> id no longer in the registry is a dead selection: every send
    ///     would route it to a provider that reports no such model. Clearing it is what makes a crash between
    ///     "connection deleted" and "default cleared" self-heal on the next boot.
    /// </remarks>
    private static (int Added, int Removed, bool DefaultCleared) Diff(StoredNodeSettings stored,
        IReadOnlyList<string> desired,
        HashSet<string> registeredIds)
    {
        var previousExternal = (stored.ToolCapableModels ?? []).Where(ExternalModelId.HasExternalScheme).ToArray();
        var defaultCleared = ExternalModelId.HasExternalScheme(stored.DefaultModelName)
                             && (ExternalModelId.Canonicalize(stored.DefaultModelName) is not { } canonicalDefault
                                 || !registeredIds.Contains(canonicalDefault));

        return (desired.Except(previousExternal, StringComparer.Ordinal).Count(),
            previousExternal.Except(desired, StringComparer.Ordinal).Count(),
            defaultCleared);
    }
}

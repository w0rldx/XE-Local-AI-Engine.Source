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
///     <para>
///         Only <c>ext:</c>-scheme keys are ever touched. That is what makes the pass safe to run unconditionally at
///         startup: a GGUF's map row, an Ollama backfill row, and an operator's hand-curated allow-list entry for a
///         local model are all invisible to it.
///     </para>
///     <para>
///         Two comparison rules are load-bearing and deliberately different. The provider map is case-INSENSITIVE, so
///         its rows are matched case-insensitively; the tool-capable allow-list is matched ORDINALLY, because
///         <c>LocalToolOfferProvider.IsToolCapable</c> compares ordinally and an entry differing only in case is not
///         capable. Both sides are fed the ONE canonical spelling the store minted, which is what lets the two rules
///         coexist without a model being routable but not tool-capable.
///     </para>
/// </remarks>
public sealed class ExternalProviderReconciler : IExternalProviderReconciler
{
    private readonly ILocalChatClientCacheInvalidator _chatClientCacheInvalidator;
    private readonly IModelProviderMapLeaseCoordinator _leaseCoordinator;
    private readonly ILogger<ExternalProviderReconciler> _logger;
    private readonly ICoordinatedModelProviderMapStore _mapStore;
    private readonly ILocalModelProviderResolver _providerResolver;
    private readonly IExternalProviderRegistry _registry;
    private readonly INodeSettingsStore _settingsStore;

    public ExternalProviderReconciler(IExternalProviderRegistry registry,
        ICoordinatedModelProviderMapStore mapStore,
        IModelProviderMapLeaseCoordinator leaseCoordinator,
        ILocalModelProviderResolver providerResolver,
        ILocalChatClientCacheInvalidator chatClientCacheInvalidator,
        INodeSettingsStore settingsStore,
        ILogger<ExternalProviderReconciler> logger)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
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
        var registrations = await _registry.ListRegistrationsAsync(cancellationToken).ConfigureAwait(false);

        // Ordinal: these ids came out of the registry, which is keyed by the canonical spelling the store minted.
        var registered = registrations.Select(registration => registration.ModelId).ToArray();
        var registeredSet = new HashSet<string>(registered, StringComparer.Ordinal);

        var (written, removed) = await ReconcileProviderMapAsync(registered, registeredSet, cancellationToken).ConfigureAwait(false);
        var (added, dropped, defaultCleared) = await ReconcileNodeSettingsAsync(registrations, registeredSet, cancellationToken).ConfigureAwait(false);

        var report = new ExternalProviderReconciliationReport(written, removed, added, dropped, defaultCleared);
        if (report.Changed)
        {
            // The resolver memoizes model→provider for a few seconds and the router caches a chat client per
            // (provider, model); a repaired row that neither of them sees is a repair that has not taken effect until
            // both caches expire.
            _providerResolver.InvalidateModelProviderMap();
            _chatClientCacheInvalidator.ClearClientCache();
            _logger.LogInformation("External provider reconciliation repaired drift: {MapWritten} map row(s) written, {MapRemoved} removed, {AllowAdded} allow-list entr(ies) added, {AllowRemoved} removed, default cleared: {DefaultCleared}.",
                report.MapRowsWritten,
                report.MapRowsRemoved,
                report.AllowListAdded,
                report.AllowListRemoved,
                report.DefaultModelCleared);
        }

        return report;
    }

    private async Task<(int Written, int Removed)> ReconcileProviderMapAsync(IReadOnlyList<string> registered,
        HashSet<string> registeredSet,
        CancellationToken cancellationToken)
    {
        // Snapshot first, repair per row under its own lease. See ICoordinatedModelProviderMapStore.ListAsync for why
        // the listing is deliberately lease-free and what re-reads under the lease.
        var rows = await _mapStore.ListAsync(cancellationToken).ConfigureAwait(false);
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

            if (await TryWriteMapRowAsync(modelId, cancellationToken).ConfigureAwait(false))
            {
                written++;
            }
        }

        // Canonicalize before the membership test: a hand-edited row spelled with a capitalized connection slug is the
        // SAME model, and must not be deleted as an orphan.
        var orphans = externalRows.Select(row => row.ModelName)
                                  .Where(modelName => ExternalModelId.Canonicalize(modelName) is not { } canonical
                                                      || !registeredSet.Contains(canonical));

        var removed = 0;
        foreach (var modelName in orphans)
        {
            if (await TryRemoveMapRowAsync(modelName, cancellationToken).ConfigureAwait(false))
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
                cancellationToken).ConfigureAwait(false);

            // Re-read UNDER the lease: the snapshot that selected this model may be stale, and the upsert needs the
            // row's current revision to compare-and-swap against.
            var current = await _mapStore.ReadWithRevisionAsync(lease, modelId, cancellationToken).ConfigureAwait(false);
            if (current is not null && string.Equals(current.ProviderName, ExternalProviderConstants.ProviderName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (current is not null)
            {
                // A non-external provider already owns this exact name. Refuse rather than steal it: the name grammar
                // makes this practically impossible (no GGUF or Ollama model can be called "ext:…"), so reaching here
                // means something is genuinely wrong and silently re-pointing the row would hide it.
                _logger.LogWarning("The provider-map row for external model '{ModelId}' is owned by provider '{ProviderName}'; leaving it untouched.",
                    modelId,
                    current.ProviderName);
                return false;
            }

            var result = await _mapStore.TryUpsertAsync(lease,
                modelId,
                ExternalProviderConstants.ProviderName,
                expectedRevision: null,
                cancellationToken).ConfigureAwait(false);
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
                cancellationToken).ConfigureAwait(false);

            var current = await _mapStore.ReadWithRevisionAsync(lease, modelName, cancellationToken).ConfigureAwait(false);
            if (current is null || !string.Equals(current.ProviderName, ExternalProviderConstants.ProviderName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var result = await _mapStore.TryRemoveIfMatchAsync(lease,
                current.ModelName,
                current.ProviderName,
                current.Revision,
                cancellationToken).ConfigureAwait(false);
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

    private async Task<(int Added, int Removed, bool DefaultCleared)> ReconcileNodeSettingsAsync(
        IReadOnlyList<ExternalProviderModelRegistration> registrations,
        HashSet<string> registeredSet,
        CancellationToken cancellationToken)
    {
        var stored = await _settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var existing = stored.ToolCapableModels ?? [];

        // Every non-external entry keeps its exact spelling AND its position: the allow-list is operator-curated, and
        // an auto-sync that reordered or re-cased a hand-added local model would look like data loss.
        var preserved = existing.Where(entry => !ExternalModelId.HasExternalScheme(entry)).ToArray();
        var desired = registrations.Where(registration => registration.Model.SupportsTools)
                                   .Select(registration => registration.ModelId)
                                   .Distinct(StringComparer.Ordinal)
                                   .ToArray();

        var previousExternal = existing.Where(ExternalModelId.HasExternalScheme).ToArray();
        var merged = new List<string>(preserved.Length + desired.Length);
        merged.AddRange(preserved);
        merged.AddRange(desired);

        var added = desired.Except(previousExternal, StringComparer.Ordinal).Count();
        var removed = previousExternal.Except(desired, StringComparer.Ordinal).Count();

        // A DefaultModelName that is an ext: id no longer in the registry is a dead selection: every send would route
        // it to a provider that reports no such model. Clearing it here is what makes a crash between "connection
        // deleted" and "default cleared" self-heal on the next boot.
        var defaultCleared = ExternalModelId.HasExternalScheme(stored.DefaultModelName)
                             && (ExternalModelId.Canonicalize(stored.DefaultModelName) is not { } canonicalDefault
                                 || !registeredSet.Contains(canonicalDefault));

        if (added == 0 && removed == 0 && !defaultCleared)
        {
            return (0, 0, false);
        }

        // Written as one save: SaveAsync invalidates and re-primes the settings cache, and this pass runs on every
        // boot, so two writes for one reconciliation would churn that cache for nothing.
        await _settingsStore.SaveAsync(stored with
        {
            ToolCapableModels = merged,
            DefaultModelName = defaultCleared ? null : stored.DefaultModelName
        }, cancellationToken).ConfigureAwait(false);

        if (defaultCleared)
        {
            _logger.LogInformation("Cleared the node default model: '{ModelName}' is an external model that is no longer registered.", stored.DefaultModelName);
        }

        return (added, removed, defaultCleared);
    }
}

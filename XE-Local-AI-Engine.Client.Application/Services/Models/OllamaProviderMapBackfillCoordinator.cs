namespace XE_Local_AI_Engine.Client.Services.Models;

using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Providers.Ollama.Contracts;
using XE_Local_AI_Engine.Providers.Ollama.Implementation;

public sealed class OllamaProviderMapBackfillCoordinator : IOllamaProviderMapBackfillCoordinator
{
    private readonly IModelProviderMapLeaseCoordinator _leaseCoordinator;
    private readonly ILogger<OllamaProviderMapBackfillCoordinator> _logger;
    private readonly ICoordinatedModelProviderMapStore _mapStore;
    private readonly IOllamaModelService _ollamaModelService;
    private readonly ILocalModelProviderResolver _providerResolver;

    public OllamaProviderMapBackfillCoordinator(
        IOllamaModelService ollamaModelService,
        IModelProviderMapLeaseCoordinator leaseCoordinator,
        ICoordinatedModelProviderMapStore mapStore,
        ILocalModelProviderResolver providerResolver,
        ILogger<OllamaProviderMapBackfillCoordinator> logger)
    {
        ArgumentNullException.ThrowIfNull(leaseCoordinator);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(mapStore);
        ArgumentNullException.ThrowIfNull(ollamaModelService);
        ArgumentNullException.ThrowIfNull(providerResolver);
        _leaseCoordinator = leaseCoordinator;
        _logger = logger;
        _mapStore = mapStore;
        _ollamaModelService = ollamaModelService;
        _providerResolver = providerResolver;
    }

    public async Task<int> BackfillAsync(CancellationToken cancellationToken = default)
    {
        var installedModelNames = await ListInstalledNamesAsync(cancellationToken);
        var mapped = 0;
        foreach (var modelName in installedModelNames)
        {
            try
            {
                await using var lease = await _leaseCoordinator.AcquireMapMutationAsync(modelName,
                    ModelProviderMapMutationKind.Backfill,
                    cancellationToken);

                var currentInventory = await ListInstalledNamesAsync(cancellationToken);
                if (!currentInventory.Contains(modelName, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                var existing = await _mapStore.ReadWithRevisionAsync(lease, modelName, cancellationToken);
                if (existing is not null)
                {
                    continue;
                }

                var result = await _mapStore.TryUpsertAsync(lease,
                    modelName,
                    OllamaLocalModelProvider.OllamaProviderName,
                    expectedRevision: null,
                    cancellationToken);
                if (result is ProviderMapMutationResult.Mutated)
                {
                    mapped++;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException)
            {
                _logger.LogWarning(exception, "Could not backfill the Ollama provider mapping for an installed model; skipping it.");
            }
        }

        if (mapped > 0)
        {
            _providerResolver.InvalidateModelProviderMap();
        }

        return mapped;
    }

    private async Task<IReadOnlyList<string>> ListInstalledNamesAsync(CancellationToken cancellationToken)
    {
        var installed = await _ollamaModelService.ListLocalModelsAsync(cancellationToken);
        return installed.Select(static model => model.Name)
                        .Where(static name => !string.IsNullOrWhiteSpace(name))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Order(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
    }
}

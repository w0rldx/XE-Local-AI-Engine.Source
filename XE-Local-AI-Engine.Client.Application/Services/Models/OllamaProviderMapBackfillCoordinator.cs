namespace XE_Local_AI_Engine.Client.Services.Models;

using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Providers.Ollama.Implementation;

public sealed class OllamaProviderMapBackfillCoordinator(
    IOllamaModelService ollamaModelService,
    IModelProviderMapLeaseCoordinator leaseCoordinator,
    ICoordinatedModelProviderMapStore mapStore,
    ILocalModelProviderResolver providerResolver,
    ILogger<OllamaProviderMapBackfillCoordinator> logger) : IOllamaProviderMapBackfillCoordinator
{
    private readonly IModelProviderMapLeaseCoordinator _leaseCoordinator = leaseCoordinator ?? throw new ArgumentNullException(nameof(leaseCoordinator));
    private readonly ILogger<OllamaProviderMapBackfillCoordinator> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly ICoordinatedModelProviderMapStore _mapStore = mapStore ?? throw new ArgumentNullException(nameof(mapStore));
    private readonly IOllamaModelService _ollamaModelService = ollamaModelService ?? throw new ArgumentNullException(nameof(ollamaModelService));
    private readonly ILocalModelProviderResolver _providerResolver = providerResolver ?? throw new ArgumentNullException(nameof(providerResolver));

    public async Task<int> BackfillAsync(CancellationToken cancellationToken = default)
    {
        var installedModelNames = await ListInstalledNamesAsync(cancellationToken).ConfigureAwait(false);
        var mapped = 0;
        foreach (var modelName in installedModelNames)
        {
            try
            {
                await using var lease = await _leaseCoordinator.AcquireMapMutationAsync(modelName,
                    ModelProviderMapMutationKind.Backfill,
                    cancellationToken).ConfigureAwait(false);

                var currentInventory = await ListInstalledNamesAsync(cancellationToken).ConfigureAwait(false);
                if (!currentInventory.Contains(modelName, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                var existing = await _mapStore.ReadWithRevisionAsync(lease, modelName, cancellationToken).ConfigureAwait(false);
                if (existing is not null)
                {
                    continue;
                }

                var result = await _mapStore.TryUpsertAsync(lease,
                    modelName,
                    OllamaLocalModelProvider.OllamaProviderName,
                    expectedRevision: null,
                    cancellationToken).ConfigureAwait(false);
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
        var installed = await _ollamaModelService.ListLocalModelsAsync(cancellationToken).ConfigureAwait(false);
        return installed.Select(static model => model.Name)
                        .Where(static name => !string.IsNullOrWhiteSpace(name))
                        .Select(static name => name!)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Order(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
    }
}

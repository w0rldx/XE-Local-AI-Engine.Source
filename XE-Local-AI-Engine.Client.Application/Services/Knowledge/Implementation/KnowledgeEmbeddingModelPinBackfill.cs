namespace XE_Local_AI_Engine.Client.Services.Knowledge.Implementation;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     One-shot startup backfill of the knowledge-base embedding pin for a corpus indexed before the pin existed.
/// </summary>
/// <remarks>
///     Pins only when nothing is pinned, the Indexed corpus names exactly ONE embedding model, and that model is installed:
///     it adopts the model that really built the vectors, even where the configured default never matched it by name. A
///     mixed corpus or a missing model leaves the pin unset, and the next successful index commit pins instead.
///     Accepted ceiling: a document already pending at startup may finish indexing before this runs; its commit then pins
///     through the same write-once path, so the outcome is one pin either way. Best-effort: a failure never blocks startup.
/// </remarks>
public sealed class KnowledgeEmbeddingModelPinBackfill : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly INodeSettingsStore _settingsStore;
    private readonly ILocalModelProviderResolver _providerResolver;
    private readonly KnowledgeBaseOptions _options;
    private readonly ILogger<KnowledgeEmbeddingModelPinBackfill> _logger;

    public KnowledgeEmbeddingModelPinBackfill(IServiceScopeFactory scopeFactory,
        INodeSettingsStore settingsStore,
        ILocalModelProviderResolver providerResolver,
        IOptions<KnowledgeBaseOptions> options,
        ILogger<KnowledgeEmbeddingModelPinBackfill> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _providerResolver = providerResolver ?? throw new ArgumentNullException(nameof(providerResolver));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Yield first so host startup never waits on the model inventory.
        await Task.Yield();

        try
        {
            _ = await BackfillOnceAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down during startup; the backfill retries on the next start.
        }
        catch (Exception exception)
        {
            _logger.LogWarning("The knowledge embedding pin backfill failed ({ExceptionType}); the next index commit pins instead.",
                exception.GetType().Name);
        }
    }

    /// <summary>One backfill pass; true when it wrote the pin. Internal so a test can drive it without the hosted lifecycle.</summary>
    internal async Task<bool> BackfillOnceAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace((await _settingsStore.LoadAsync(cancellationToken)).KnowledgeEmbeddingModelName))
        {
            return false;
        }

        IReadOnlyList<string> corpusModels;
        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            // Two rows are enough to tell "exactly one" from "mixed".
            corpusModels = await scope.ServiceProvider.GetRequiredService<IKnowledgeDocumentCatalogService>()
                                      .ListIndexedEmbeddingModelsAsync(limit: 2, cancellationToken);
        }

        if (corpusModels.Count != 1 || string.IsNullOrWhiteSpace(corpusModels[0]))
        {
            if (corpusModels.Count > 1)
            {
                _logger.LogInformation("The knowledge corpus was indexed with more than one embedding model; no embedding pin was backfilled.");
            }

            return false;
        }

        var corpusModel = corpusModels[0];
        var installed = await _providerResolver.ResolveProvider(_options.EmbeddingProviderName).ListModelsAsync(cancellationToken);
        if (!installed.Any(descriptor => string.Equals(descriptor.ModelName, corpusModel, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var wrote = await KnowledgeEmbeddingModelPin.PinIfUnsetAsync(_settingsStore, corpusModel, cancellationToken);
        if (wrote)
        {
            _logger.LogInformation("Pinned the knowledge embedding model {ModelName}, the one model the indexed corpus was built with.", corpusModel);
        }

        return wrote;
    }
}

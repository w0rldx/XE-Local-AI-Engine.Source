namespace XE_Local_AI_Engine.Client.Services.ModelFit.Implementation;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>
///     Default <see cref="IModelFitQueryService" />: a thin cache reader composing the sanitized snapshot-summary
///     store and the normalized recommendation store. It takes NO dependency on the refresh service, so a read can
///     never start an advisor run, and none on approved images.
/// </summary>
/// <remarks>
///     On the read path it reconciles each row's install state against the node's installed GGUF models. Listing them is a node-local
///     read, NOT an advisor run, so the no-runner invariant still holds. The enrichment is best-effort: if the install
///     list cannot be read, each row keeps its stored flag.
/// </remarks>
public sealed class ModelFitQueryService : IModelFitQueryService
{
    /// <summary>Member of the snapshot's <c>system</c> diagnostics object listing the catalog entries the run skipped.</summary>
    internal const string SkippedCatalogEntriesMember = "skipped_catalog_entries";

    private readonly ILogger<ModelFitQueryService> _logger;
    private readonly ILocalModelProviderResolver _providerResolver;
    private readonly IModelFitRecommendationStore _recommendationStore;
    private readonly IModelFitSnapshotStore _snapshotStore;

    public ModelFitQueryService(IModelFitSnapshotStore snapshotStore,
        IModelFitRecommendationStore recommendationStore,
        ILocalModelProviderResolver providerResolver,
        ILogger<ModelFitQueryService> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(providerResolver);
        ArgumentNullException.ThrowIfNull(recommendationStore);
        ArgumentNullException.ThrowIfNull(snapshotStore);
        _logger = logger;
        _providerResolver = providerResolver;
        _recommendationStore = recommendationStore;
        _snapshotStore = snapshotStore;
    }

    public async Task<ModelFitLatestRecommendationsView?> GetLatestRecommendationsAsync(string? useCase,
        string providerName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);

        // Cache-only: the latest successful recommendation snapshot for this key. A recommendation snapshot has a null
        // model-name (the latest-successful key matches on null), so model-name is fixed to null here.
        var summary = await _snapshotStore.GetLatestSuccessfulSummaryAsync(ModelFitOperation.Recommend,
            useCase,
            providerName,
            modelName: null,
            cancellationToken);

        if (summary is null)
        {
            // No cached recommendation snapshot has ever succeeded for this key — the caller surfaces the empty state.
            return null;
        }

        var recommendations = await _recommendationStore.ListForSnapshotAsync(summary.Id, cancellationToken);
        recommendations = await ApplyNodeInstallStateAsync(recommendations, cancellationToken);
        var diagnosticsJson = await _snapshotStore.GetDiagnosticsJsonByIdAsync(summary.Id, cancellationToken);

        return new ModelFitLatestRecommendationsView
        {
            SnapshotId = summary.Id,
            Status = summary.Status,
            ApprovedImageId = summary.ApprovedImageId,
            UseCase = summary.UseCase,
            ProviderName = summary.ProviderName,
            CompletedAtUtc = summary.CompletedAtUtc,
            Recommendations =
            [
                .. recommendations.Select(static record => new ModelFitRecommendationView
                {
                    Record = record,
                    Diagnostics = ModelFitRecommendationDiagnostics.Parse(record.DiagnosticsJson, record.ModelName)
                })
            ],
            SkippedCatalogEntries = ReadSkippedCatalogEntries(diagnosticsJson)
        };
    }

    // Tolerant: an older snapshot has no such member, and null or malformed diagnostics read as none skipped.
    private static IReadOnlyList<string> ReadSkippedCatalogEntries(string? diagnosticsJson)
    {
        if (string.IsNullOrWhiteSpace(diagnosticsJson))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(diagnosticsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty(SkippedCatalogEntriesMember, out var entries)
                || entries.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return [.. entries.EnumerateArray().Where(static entry => entry.ValueKind == JsonValueKind.String).Select(static entry => entry.GetString()!)];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    ///     Returns <paramref name="recommendations" /> with each row's <c>IsInstalled</c> set from the GGUFs installed
    ///     now rather than the flag stored when the snapshot ran.
    /// </summary>
    /// <remarks>
    ///     A row is installed iff its <c>PullModelName</c> (the <c>{repo}:{quant}</c> name both the advisor and the GGUF
    ///     store format with <see cref="GgufModelName" />) names an installed model, case-insensitively. A row with a null
    ///     <c>PullModelName</c> is reported not-installed. Best-effort: if the install list cannot be read the stored
    ///     flags are returned as-is.
    /// </remarks>
    private async Task<IReadOnlyList<ModelFitRecommendationRecord>> ApplyNodeInstallStateAsync(IReadOnlyList<ModelFitRecommendationRecord> recommendations,
        CancellationToken cancellationToken)
    {
        if (recommendations.Count == 0)
        {
            return recommendations;
        }

        HashSet<string> installedNames;
        try
        {
            var installed = await _providerResolver.ResolveProvider(LlamaServerProviderConstants.ProviderName).ListModelsAsync(cancellationToken);
            installedNames = installed.Select(static model => model.ModelName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception)
        {
            // Node-local enrichment only: a store failure must never fail the cached read, so fall back to the snapshot's stored install flags.
            _logger.LogDebug(exception, "Could not list installed models for install-state enrichment; using stored flags.");
            return recommendations;
        }

        return recommendations
               .Select(recommendation => recommendation with
               {
                   IsInstalled = recommendation.PullModelName is { } pullModelName && installedNames.Contains(pullModelName.Trim())
               })
               .ToList();
    }
}

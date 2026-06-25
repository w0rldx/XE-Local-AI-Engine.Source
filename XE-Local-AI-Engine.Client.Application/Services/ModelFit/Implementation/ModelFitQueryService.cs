namespace XE_Local_AI_Engine.Client.Services.ModelFit.Implementation;

using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     Default <see cref="IModelFitQueryService" />: a thin cache reader over the model-fit stores. It composes the
///     sanitized snapshot-summary store and the normalized recommendation store. It takes NO dependency on the refresh
///     service, so a read can never start an advisor run. The approved-image store dependency is gone (the approved-image
///     concept was removed when the advisor replaced the Docker/llmfit recommendation backend).
///     <para>
///         On the read path it also reconciles each row's install state against the node's actually-installed Ollama
///         models (<see cref="IOllamaModelService.ListLocalModelsAsync" />). Listing the node's installed models is a
///         node-local read — NOT an advisor run — so the no-runner invariant still holds. The enrichment is best-effort:
///         if the install list can't be read, each row keeps its stored flag.
///     </para>
/// </summary>
public sealed class ModelFitQueryService(
    IModelFitSnapshotStore snapshotStore,
    IModelFitRecommendationStore recommendationStore,
    IOllamaModelService ollamaModelService,
    ILogger<ModelFitQueryService> logger) : IModelFitQueryService
{
    private readonly ILogger<ModelFitQueryService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IOllamaModelService _ollamaModelService = ollamaModelService ?? throw new ArgumentNullException(nameof(ollamaModelService));
    private readonly IModelFitRecommendationStore _recommendationStore = recommendationStore ?? throw new ArgumentNullException(nameof(recommendationStore));
    private readonly IModelFitSnapshotStore _snapshotStore = snapshotStore ?? throw new ArgumentNullException(nameof(snapshotStore));

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
                                              cancellationToken)
                                          .ConfigureAwait(false);

        if (summary is null)
        {
            // No cached recommendation snapshot has ever succeeded for this key — the caller surfaces the empty state.
            return null;
        }

        var recommendations = await _recommendationStore.ListForSnapshotAsync(summary.Id, cancellationToken).ConfigureAwait(false);
        recommendations = await ApplyNodeInstallStateAsync(recommendations, cancellationToken).ConfigureAwait(false);

        return new ModelFitLatestRecommendationsView(summary.Id,
            summary.Status,
            summary.ApprovedImageId,
            summary.UseCase,
            summary.ProviderName,
            summary.CompletedAtUtc,
            recommendations);
    }

    /// <summary>
    ///     Returns <paramref name="recommendations" /> with each row's <c>IsInstalled</c> set from the node's actually-
    ///     installed Ollama models rather than llmfit's offline flag. A row is installed iff its <c>PullModelName</c> (the
    ///     exact Ollama tag) matches an installed tag (case-insensitive, with <c>:latest</c> elided so a bare name matches
    ///     its tagged form). Rows with a null <c>PullModelName</c> have no tag to match and are reported not-installed.
    ///     Best-effort: if the install list cannot be read (e.g. Ollama unreachable) the stored flags are returned as-is.
    /// </summary>
    private async Task<IReadOnlyList<ModelFitRecommendationRecord>> ApplyNodeInstallStateAsync(IReadOnlyList<ModelFitRecommendationRecord> recommendations,
        CancellationToken cancellationToken)
    {
        if (recommendations.Count == 0)
        {
            return recommendations;
        }

        HashSet<string> installedTags;
        try
        {
            var installed = await _ollamaModelService.ListLocalModelsAsync(cancellationToken).ConfigureAwait(false);
            installedTags = installed
                            .Select(model => model.Name)
                            .OfType<string>()
                            .Where(name => !string.IsNullOrWhiteSpace(name))
                            .Select(NormalizeTag)
                            .ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception exception)
        {
            // Node-local enrichment only: a provider/transport failure (e.g. Ollama unreachable) must never fail the
            // cached read — fall back to the snapshot's stored install flags.
            _logger.LogDebug(exception, "Could not list installed models for install-state enrichment; using stored flags.");
            return recommendations;
        }

        return recommendations
               .Select(recommendation => recommendation with
               {
                   IsInstalled = IsInstalledOnNode(recommendation.PullModelName, installedTags)
               })
               .ToList();
    }

    private static bool IsInstalledOnNode(string? pullModelName, HashSet<string> installedTags)
    {
        return !string.IsNullOrWhiteSpace(pullModelName) && installedTags.Contains(NormalizeTag(pullModelName));
    }

    /// <summary>
    ///     Canonical Ollama tag for matching: trimmed, lower-cased, with an explicit <c>:latest</c> tag elided so a bare
    ///     name matches its <c>:latest</c> form (<c>qwen3-coder</c> ≡ <c>qwen3-coder:latest</c>) while distinct tags such
    ///     as <c>llama3:8b</c> stay distinct.
    /// </summary>
    private static string NormalizeTag(string tag)
    {
        // Upper-invariant (CA1308: upper-casing round-trips safely) so matching is case-insensitive via an ordinal set.
        var normalized = tag.Trim().ToUpperInvariant();
        return normalized.EndsWith(":LATEST", StringComparison.Ordinal) ? normalized[..^":LATEST".Length] : normalized;
    }
}

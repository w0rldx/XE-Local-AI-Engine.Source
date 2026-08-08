namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1.Mappers;

using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Monitoring;

internal static class PlaybookMonitorMapper
{
    public static AgentPlaybookMonitorResponse ToResponse(this IReadOnlyList<PlaybookActionMonitorView> views,
        PlaybookRetrievalOptions retrievalOptions)
    {
        ArgumentNullException.ThrowIfNull(views);
        ArgumentNullException.ThrowIfNull(retrievalOptions);

        // An embedding model name turns on the embedding ranker; blank keeps the model-free lexical ranker (the embedding-ranker configuration).
        var embeddingActive = !string.IsNullOrWhiteSpace(retrievalOptions.EmbeddingModelName);
        var ranker = embeddingActive ? "embedding" : "lexical";
        var embeddingModel = embeddingActive ? retrievalOptions.EmbeddingModelName : null;

        return new AgentPlaybookMonitorResponse([
                .. views.Select(static view => new PlaybookActionMonitorItemResponse(view.ActionId,
                    view.EnabledAtUtc,
                    view.BeforeDownRate,
                    view.AfterDownRate,
                    view.AfterSampleSize,
                    view.Status,
                    view.Flagged,
                    view.FacetToolName))
            ],
            new PlaybookRetrievalResponse(retrievalOptions.RetrievalThreshold, retrievalOptions.TopK, ranker, embeddingModel));
    }
}

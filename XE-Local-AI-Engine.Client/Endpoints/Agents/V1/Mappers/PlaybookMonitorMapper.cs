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

        return new AgentPlaybookMonitorResponse
        {
            Items = [
                .. views.Select(static view => new PlaybookActionMonitorItemResponse
                {
                    ActionId = view.ActionId,
                    EnabledAtUtc = view.EnabledAtUtc,
                    BeforeDownRate = view.BeforeDownRate,
                    AfterDownRate = view.AfterDownRate,
                    AfterSampleSize = view.AfterSampleSize,
                    Status = view.Status,
                    Flagged = view.Flagged,
                    FacetToolName = view.FacetToolName
                })
            ],
            Retrieval = new PlaybookRetrievalResponse { Threshold = retrievalOptions.RetrievalThreshold, TopK = retrievalOptions.TopK, Ranker = ranker, EmbeddingModel = embeddingModel }
        };
    }
}

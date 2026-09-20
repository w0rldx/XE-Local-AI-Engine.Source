namespace XE_Local_AI_Engine.Client.Endpoints.Knowledge.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>
///     Begins the one-click download of the recommended embedding model (<see cref="RecommendedEmbeddingModel" />), so
///     a fresh node can index knowledge-base documents at all. Body-less, so no JSON body is expected.
/// </summary>
/// <remarks>
///     The exact mirror of <see cref="DownloadRecommendedRerankerEndpoint" />, delegating to <see cref="IGgufDownloadCoordinator" /> so progress and cancel stream over the
///     GGUF download hub. Not merely a convenience: the reranker is optional, but with no embedding model installed, ingestion of every document fails outright and the
///     knowledge base is inert. The already-installed check is broader than the reranker's, because the question is whether this node can embed AT ALL and
///     <c>EmbeddingModelResolver</c> resolves ANY installed embedding-named model — so
///     <see cref="DownloadRecommendedEmbeddingResponse.AlreadyInstalled" /> reports the model that would actually be used.
/// </remarks>
public sealed class DownloadRecommendedEmbeddingEndpoint : EndpointWithoutRequest<DownloadRecommendedEmbeddingResponse>
{
    private readonly IGgufDownloadCoordinator _downloadCoordinator;
    private readonly IGgufModelStore _modelStore;

    public DownloadRecommendedEmbeddingEndpoint(
        IGgufDownloadCoordinator downloadCoordinator,
        IGgufModelStore modelStore)
    {
        ArgumentNullException.ThrowIfNull(downloadCoordinator);
        ArgumentNullException.ThrowIfNull(modelStore);
        _downloadCoordinator = downloadCoordinator;
        _modelStore = modelStore;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.KnowledgeBase.EmbeddingDownloadRecommended);
        Policies(NodeAuthorizationPolicies.Operator);
        // GgufDownloadExceptionHandler maps the synchronous acquisition/HF failures to these ProblemDetails statuses.
        Description(builder => builder.ProducesProblem(StatusCodes.Status403Forbidden)
                                      .ProducesProblem(StatusCodes.Status404NotFound)
                                      .ProducesProblem(StatusCodes.Status409Conflict)
                                      .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
                                      .ProducesProblem(StatusCodes.Status507InsufficientStorage));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        // Already-usable friendly no-op. Which installed model counts as "this node can already embed" is
        // RecommendedEmbeddingModel's own two-step rule (recommended repo first, else any embedding-named model).
        var existing = await RecommendedEmbeddingModel.ResolveExistingAsync(_modelStore, ct);
        if (existing is not null)
        {
            await Send.OkAsync(new DownloadRecommendedEmbeddingResponse
                {
                    ModelName = existing.ModelName,
                    RepoId = RecommendedEmbeddingModel.RepoId,
                    Quant = RecommendedEmbeddingModel.Quant,
                    AlreadyInstalled = true,
                    AlreadyInFlight = false
                },
                ct);
            return;
        }

        // Start (or rejoin) the download through the coordinator's detached path — the SAME path an operator-initiated
        // GGUF download uses, so progress/cancel and the model_provider_map write happen through one code path.
        var ticket = await _downloadCoordinator.StartAsync(RecommendedEmbeddingModel.ToDownloadRequest(), ct);

        await Send.OkAsync(new DownloadRecommendedEmbeddingResponse
            {
                ModelName = ticket.ModelName,
                RepoId = RecommendedEmbeddingModel.RepoId,
                Quant = RecommendedEmbeddingModel.Quant,
                AlreadyInstalled = false,
                AlreadyInFlight = ticket.AlreadyInFlight
            },
            ct);
    }
}

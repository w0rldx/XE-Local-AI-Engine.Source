namespace XE_Local_AI_Engine.Client.Endpoints.Knowledge.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>
///     FastEndpoints handler that begins the one-click download of the recommended embedding model
///     (<see cref="RecommendedEmbeddingModel" />) so a fresh node can index knowledge-base documents at all (POST).
///     Deliberately the exact mirror of <see cref="DownloadRecommendedRerankerEndpoint" />, down to the response shape:
///     thin transport over the SAME machinery an operator HF download uses, delegating to the
///     <see cref="IGgufDownloadCoordinator" /> so progress/cancel stream over the GGUF download hub. Body-less
///     (<c>EndpointWithoutRequest</c>) — no JSON body is expected.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why this is not merely a convenience.</b> The reranker is optional — without it, search silently degrades
///         to fusion order. The embedder is load-bearing: with no embedding model installed, ingestion of every document
///         fails outright and the knowledge base is inert. Before this endpoint existed, the only route out was to know
///         to go to Models → Browse Hugging Face and pick a suitable embedding GGUF by hand, which the failure message
///         did not say (F-020, live eval 2026-07-31).
///     </para>
///     <para>
///         The already-installed check is broader than the reranker's on purpose. The reranker asks only "is THE
///         recommended repo present", because selecting a reranker is an explicit operator act. Here the question that
///         actually matters is "can this node embed at all", and <c>EmbeddingModelResolver</c> will happily resolve ANY
///         installed embedding-named model — so if the operator already has, say, an <c>mxbai-embed</c> GGUF, offering
///         to download a second one would be noise. <see cref="DownloadRecommendedEmbeddingResponse.AlreadyInstalled" />
///         therefore reports the model that would actually be used, which is not necessarily the recommended one.
///     </para>
/// </remarks>
public sealed class DownloadRecommendedEmbeddingEndpoint(
    IGgufDownloadCoordinator downloadCoordinator,
    IGgufModelStore modelStore)
    : EndpointWithoutRequest<DownloadRecommendedEmbeddingResponse>
{
    private readonly IGgufDownloadCoordinator _downloadCoordinator = downloadCoordinator ?? throw new ArgumentNullException(nameof(downloadCoordinator));
    private readonly IGgufModelStore _modelStore = modelStore ?? throw new ArgumentNullException(nameof(modelStore));

    public override void Configure()
    {
        Post(LocalApiRoutes.KnowledgeBase.EmbeddingDownloadRecommended);
        Policies(NodeAuthorizationPolicies.Operator);
        // GgufDownloadEndpointSupport maps the synchronous acquisition/HF failures to these ProblemDetails statuses.
        Description(builder => builder.ProducesProblem(StatusCodes.Status403Forbidden)
                                      .ProducesProblem(StatusCodes.Status404NotFound)
                                      .ProducesProblem(StatusCodes.Status409Conflict)
                                      .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
                                      .ProducesProblem(StatusCodes.Status507InsufficientStorage));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        // Already-usable friendly no-op: check the LOCAL registry (no network resolve). Prefer the recommended repo when
        // present so the reported identity is stable, but accept ANY installed embedding-named model — that is exactly
        // what EmbeddingModelResolver would pick, so re-downloading would not change behaviour.
        var installed = await _modelStore.ListInstalledModelsAsync(ct).ConfigureAwait(false);
        var existing = installed.FirstOrDefault(model => RecommendedEmbeddingModel.Matches(model.ModelName))
                       ?? installed
                          .Where(model => !string.IsNullOrWhiteSpace(model.ModelName)
                                          && ModelKindDetector.IsEmbeddingName(model.ModelName))
                          .OrderBy(model => model.ModelName, StringComparer.OrdinalIgnoreCase)
                          .FirstOrDefault();

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
                ct).ConfigureAwait(false);
            return;
        }

        // Start (or rejoin) the download through the coordinator's detached path — the SAME path an operator-initiated
        // GGUF download uses, so progress/cancel and the model_provider_map write happen through one code path.
        GgufDownloadTicket ticket;
        try
        {
            ticket = await _downloadCoordinator.StartAsync(RecommendedEmbeddingModel.ToDownloadRequest(), ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (GgufDownloadEndpointSupport.IsHandled(exception))
        {
            await Send.ResultAsync(GgufDownloadEndpointSupport.Error(exception)).ConfigureAwait(false);
            return;
        }

        await Send.OkAsync(new DownloadRecommendedEmbeddingResponse
            {
                ModelName = ticket.ModelName,
                RepoId = RecommendedEmbeddingModel.RepoId,
                Quant = RecommendedEmbeddingModel.Quant,
                AlreadyInstalled = false,
                AlreadyInFlight = ticket.AlreadyInFlight
            },
            ct).ConfigureAwait(false);
    }
}

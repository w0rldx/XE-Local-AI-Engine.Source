namespace XE_Local_AI_Engine.Client.Endpoints.Knowledge.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>
///     Begins the one-click download of the recommended cross-encoder reranker
///     (<see cref="RecommendedRerankerModel" />), so an operator can turn on KB reranking without hunting for a repo
///     and quant. Body-less, so no JSON body is expected.
/// </summary>
/// <remarks>
///     Thin transport over the SAME machinery an operator HF download uses: it delegates to
///     <see cref="IGgufDownloadCoordinator" />, so progress and cancel stream over the GGUF download hub, and the
///     model name it registers under carries the <c>reranker</c> fragment, so it classifies as a reranker and stays
///     out of the chat picker. Idempotent-safe, with the three outcomes described on
///     <see cref="DownloadRecommendedRerankerResponse" />. No path or token is accepted or returned.
/// </remarks>
public sealed class DownloadRecommendedRerankerEndpoint : EndpointWithoutRequest<DownloadRecommendedRerankerResponse>
{
    private readonly IGgufDownloadCoordinator _downloadCoordinator;
    private readonly IGgufModelStore _modelStore;

    public DownloadRecommendedRerankerEndpoint(
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
        Post(LocalApiRoutes.KnowledgeBase.RerankerDownloadRecommended);
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
        // Already-installed friendly no-op: RecommendedRerankerModel's rule is deliberately the narrow one — only the
        // recommended repo counts, because choosing a reranker is an explicit operator act.
        var existing = await RecommendedRerankerModel.ResolveExistingAsync(_modelStore, ct);
        if (existing is not null)
        {
            await Send.OkAsync(new DownloadRecommendedRerankerResponse
                {
                    ModelName = existing.ModelName,
                    RepoId = RecommendedRerankerModel.RepoId,
                    Quant = RecommendedRerankerModel.Quant,
                    AlreadyInstalled = true,
                    AlreadyInFlight = false
                },
                ct);
            return;
        }

        // Start (or rejoin) the download through the coordinator's detached path — the SAME path an operator-initiated
        // GGUF download uses, so progress/cancel and the model_provider_map write happen through one code path.
        var ticket = await _downloadCoordinator.StartAsync(RecommendedRerankerModel.ToDownloadRequest(), ct);

        await Send.OkAsync(new DownloadRecommendedRerankerResponse
            {
                ModelName = ticket.ModelName,
                RepoId = RecommendedRerankerModel.RepoId,
                Quant = RecommendedRerankerModel.Quant,
                AlreadyInstalled = false,
                AlreadyInFlight = ticket.AlreadyInFlight
            },
            ct);
    }
}

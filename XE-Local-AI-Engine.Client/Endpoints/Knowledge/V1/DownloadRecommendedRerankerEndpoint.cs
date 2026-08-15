namespace XE_Local_AI_Engine.Client.Endpoints.Knowledge.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>
///     FastEndpoints handler that begins the one-click download of the recommended cross-encoder reranker
///     (<see cref="RecommendedRerankerModel" />) so an operator can turn on KB reranking without hunting for a repo/quant
///     (POST). Thin transport over the SAME machinery an operator HF download uses: it delegates to the
///     <see cref="IGgufDownloadCoordinator" /> (progress/cancel then stream over the GGUF download hub), and the model
///     name it registers under carries the <c>reranker</c> fragment so it classifies as a reranker and stays out of the
///     chat picker. Body-less (<c>EndpointWithoutRequest</c>) — no JSON body is expected.
/// </summary>
/// <remarks>
///     Idempotent-safe: if the recommended reranker is already installed it is a friendly no-op (no download started,
///     <see cref="DownloadRecommendedRerankerResponse.AlreadyInstalled" /> is <c>true</c>); if a download for the same
///     model is already running the coordinator rejoins it (<see cref="DownloadRecommendedRerankerResponse.AlreadyInFlight" />
///     is <c>true</c>) rather than starting a second. No path/token is accepted or returned.
/// </remarks>
public sealed class DownloadRecommendedRerankerEndpoint(
    IGgufDownloadCoordinator downloadCoordinator,
    IGgufModelStore modelStore)
    : EndpointWithoutRequest<DownloadRecommendedRerankerResponse>
{
    private readonly IGgufDownloadCoordinator _downloadCoordinator = downloadCoordinator ?? throw new ArgumentNullException(nameof(downloadCoordinator));
    private readonly IGgufModelStore _modelStore = modelStore ?? throw new ArgumentNullException(nameof(modelStore));

    public override void Configure()
    {
        Post(LocalApiRoutes.KnowledgeBase.RerankerDownloadRecommended);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        // Already-installed friendly no-op: check the LOCAL registry (no network resolve) for any quant of the
        // recommended repo. Reranking only needs the model present; re-downloading it would be wasted bytes.
        var installed = await _modelStore.ListInstalledModelsAsync(ct).ConfigureAwait(false);
        var existing = installed.FirstOrDefault(model => RecommendedRerankerModel.Matches(model.ModelName));
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
                ct).ConfigureAwait(false);
            return;
        }

        // Start (or rejoin) the download through the coordinator's detached path — the SAME path an operator-initiated
        // GGUF download uses, so progress/cancel and the model_provider_map write happen through one code path.
        GgufDownloadTicket ticket;
        try
        {
            ticket = await _downloadCoordinator.StartAsync(RecommendedRerankerModel.ToDownloadRequest(), ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (GgufDownloadEndpointSupport.IsHandled(exception))
        {
            await Send.ResultAsync(GgufDownloadEndpointSupport.Error(exception)).ConfigureAwait(false);
            return;
        }

        await Send.OkAsync(new DownloadRecommendedRerankerResponse
            {
                ModelName = ticket.ModelName,
                RepoId = RecommendedRerankerModel.RepoId,
                Quant = RecommendedRerankerModel.Quant,
                AlreadyInstalled = false,
                AlreadyInFlight = ticket.AlreadyInFlight
            },
            ct).ConfigureAwait(false);
    }
}

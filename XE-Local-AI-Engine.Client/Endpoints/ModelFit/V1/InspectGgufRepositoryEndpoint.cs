namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ModelFit.Gguf;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>
///     FastEndpoints handler for per-repo GGUF file inspection (GET model-fit/gguf/inspect): the repo's selectable
///     <c>.gguf</c> files (quant + size, with an Unsloth-Dynamic flag) so the browse UI can offer a quant picker.
/// </summary>
/// <remarks>
///     Thin transport over <see cref="IHuggingFaceGgufDiscovery.ListRepoFilesAsync" />, the header-free fast path — the
///     picker needs only quant + size, not per-file GGUF header reads. Rows are sanitized: no token, no internal URL,
///     no path. A discovery/network failure surfaces a 200 OK with an empty file list (never a 500) so the picker
///     degrades gracefully, mirroring <see cref="BrowseGgufRepositoriesEndpoint" />.
/// </remarks>
public sealed class InspectGgufRepositoryEndpoint : Endpoint<InspectGgufRepositoryRequest, InspectGgufRepositoryResponse>
{
    private readonly IHuggingFaceGgufDiscovery _discovery;
    private readonly IGgufVariantRecommender _recommender;
    private readonly ILogger<InspectGgufRepositoryEndpoint> _logger;

    public InspectGgufRepositoryEndpoint(
        IHuggingFaceGgufDiscovery discovery,
        IGgufVariantRecommender recommender,
        ILogger<InspectGgufRepositoryEndpoint> logger)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(recommender);
        ArgumentNullException.ThrowIfNull(logger);
        _discovery = discovery;
        _recommender = recommender;
        _logger = logger;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.ModelFit.GgufInspect);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(InspectGgufRepositoryRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.RepoId))
        {
            AddError("A repository id is required.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        var repoId = req.RepoId.Trim();

        try
        {
            var detail = await _discovery.ListRepoFilesAsync(repoId, ct);
            var annotations = await _recommender.AnnotateAsync(detail.Files, ct);
            // The SAME selection a download would make, not a second scan of our own: the projector is excluded from the selectable-file listings, so
            // the picker learns of it only from the discovery seam. Both calls read one TTL-cached repo listing (HfHubClient.GetRepoAsync is keyed by repo id), so this costs no extra HF round trip.
            var projector = await _discovery.FindProjectorAsync(repoId, ct);
            await Send.OkAsync(detail.ToResponse(annotations, projector), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or HuggingFaceDownloadException or TimeoutException or InvalidOperationException or OperationCanceledException)
        {
            // A discovery/network failure must not 500 the picker — surface an empty file list with no raw reason.
            // The OperationCanceledException arm covers an HttpClient request TIMEOUT (TaskCanceledException, not the caller cancellation rethrown above), which would otherwise escape and 500.
            _logger.LogWarning(exception, "GGUF repo inspection failed for repo {RepoId}.", repoId);
            await Send.OkAsync(new InspectGgufRepositoryResponse
                {
                    RepoId = repoId,
                    Files = [],
                    HasProjector = false
                },
                ct);
        }
    }
}

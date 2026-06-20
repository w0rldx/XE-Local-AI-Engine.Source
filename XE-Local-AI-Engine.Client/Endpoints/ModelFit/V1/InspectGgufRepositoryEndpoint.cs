namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     FastEndpoints handler for per-repo GGUF file inspection (GET model-fit/gguf/inspect). Thin transport over the
///     Hugging Face GGUF discovery seam <see cref="IHuggingFaceGgufDiscovery.ListRepoFilesAsync" /> (the header-free
///     fast path — the picker needs only quant + size, not per-file GGUF header reads): it returns the repo's
///     selectable <c>.gguf</c> files (quant + size, with an Unsloth-Dynamic flag) so the browse UI can offer a quant
///     picker. Returns sanitized rows only — no token, no internal URL, no path. A discovery/network failure surfaces a
///     200 OK with an empty file list (never a 500) so the picker degrades gracefully — mirroring
///     <see cref="BrowseGgufRepositoriesEndpoint" />.
/// </summary>
public sealed class InspectGgufRepositoryEndpoint(
    IHuggingFaceGgufDiscovery discovery,
    ILogger<InspectGgufRepositoryEndpoint> logger)
    : Endpoint<InspectGgufRepositoryRequest, InspectGgufRepositoryResponse>
{
    private readonly IHuggingFaceGgufDiscovery _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
    private readonly ILogger<InspectGgufRepositoryEndpoint> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

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
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        var repoId = req.RepoId.Trim();

        try
        {
            var detail = await _discovery.ListRepoFilesAsync(repoId, ct).ConfigureAwait(false);
            await Send.OkAsync(detail.ToResponse(), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or HuggingFaceDownloadException or TimeoutException or InvalidOperationException or OperationCanceledException)
        {
            // A discovery/network failure must not 500 the picker — surface an empty file list (no raw reason). The
            // OperationCanceledException arm (after the ct-cancellation rethrow above) covers an HttpClient request
            // TIMEOUT (TaskCanceledException, not caller cancellation), which would otherwise escape and 500.
            _logger.LogWarning(exception, "GGUF repo inspection failed for repo {RepoId}.", repoId);
            await Send.OkAsync(new InspectGgufRepositoryResponse
                {
                    RepoId = repoId,
                    Files = []
                },
                ct).ConfigureAwait(false);
        }
    }
}

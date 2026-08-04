namespace XE_Local_AI_Engine.Client.Endpoints.Images.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.Abstractions.Image;

/// <summary>
///     FastEndpoints handler for per-repo image weight-file inspection (GET images/models/inspect). Thin transport over
///     <see cref="IImageModelDiscovery.InspectRepoAsync" />: returns the repo's selectable <c>.gguf</c>/<c>.safetensors</c>
///     files with size and a suggested part role, so the picker can pre-fill a whole file-set instead of asking the
///     operator to type file names. Sanitized rows only — no token, no internal URL, no path. A discovery/network
///     failure surfaces a 200 OK with an empty file list (never a 500), mirroring
///     <c>InspectGgufRepositoryEndpoint</c>.
/// </summary>
public sealed class InspectImageRepositoryEndpoint(
    IImageModelDiscovery discovery,
    ILogger<InspectImageRepositoryEndpoint> logger)
    : Endpoint<InspectImageRepositoryRequest, InspectImageRepositoryResponse>
{
    private readonly IImageModelDiscovery _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
    private readonly ILogger<InspectImageRepositoryEndpoint> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public override void Configure()
    {
        Get(LocalApiRoutes.Images.ModelInspect);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(InspectImageRepositoryRequest req, CancellationToken ct)
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
            var detail = await _discovery.InspectRepoAsync(repoId, ct).ConfigureAwait(false);
            await Send.OkAsync(new InspectImageRepositoryResponse
                {
                    RepoId = detail.RepoId,
                    IsGated = detail.IsGated,
                    License = detail.License,
                    Files =
                    [
                        .. detail.Files.Select(static file => new ImageRepositoryFileResponse
                        {
                            FileName = file.FileName,
                            Format = file.Format.ToString(),
                            SizeBytes = file.SizeBytes,
                            SuggestedRole = file.SuggestedRole.ToString()
                        })
                    ]
                },
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or HuggingFaceDownloadException or TimeoutException or InvalidOperationException or OperationCanceledException)
        {
            // A discovery/network failure must not 500 the picker — surface an empty file list (no raw reason).
            _logger.LogWarning(exception, "Image model repo inspection failed for repo {RepoId}.", repoId);
            await Send.OkAsync(new InspectImageRepositoryResponse
                {
                    RepoId = repoId,
                    IsGated = false,
                    License = null,
                    Files = []
                },
                ct).ConfigureAwait(false);
        }
    }
}

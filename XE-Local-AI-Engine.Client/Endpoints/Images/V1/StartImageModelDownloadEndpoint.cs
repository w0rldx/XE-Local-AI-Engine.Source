namespace XE_Local_AI_Engine.Client.Endpoints.Images.V1;

using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Providers.Abstractions.Image;

/// <summary>
///     FastEndpoints handler that begins an image-model file-set download (POST images/models/downloads). Mirrors the
///     GGUF start-download endpoint minimally: it validates the requested file-set, kicks
///     <see cref="IImageModelStore.EnsureModelAsync" /> on a DETACHED task (the store is a singleton, safe to capture past
///     the request scope), and returns 202 immediately. Progress/cancel are deferred (follow-up: a download coordinator +
///     hub); presence surfaces via <c>GET images/models</c>. No path/token is accepted or returned. Operator-gated.
/// </summary>
public sealed class StartImageModelDownloadEndpoint(IImageModelStore modelStore, ILogger<StartImageModelDownloadEndpoint> logger)
    : Endpoint<StartImageModelDownloadRequest, StartImageModelDownloadResponse>
{
    private readonly IImageModelStore _modelStore = modelStore ?? throw new ArgumentNullException(nameof(modelStore));
    private readonly ILogger<StartImageModelDownloadEndpoint> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public override void Configure()
    {
        Post(LocalApiRoutes.Images.ModelDownloads);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(StartImageModelDownloadRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.ModelName))
        {
            AddError("A model name is required.");
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(req.RepoId))
        {
            AddError("A repository id is required.");
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        if (!Enum.TryParse<ImageModelFamily>(req.Family, ignoreCase: true, out var family) || family == ImageModelFamily.Unknown)
        {
            AddError("A valid model family is required.");
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        var kind = ImageModelKind.Txt2Img;
        if (!string.IsNullOrWhiteSpace(req.Kind) && !Enum.TryParse(req.Kind, ignoreCase: true, out kind))
        {
            AddError("The model kind is not recognized.");
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        if (req.Parts is null || req.Parts.Count == 0)
        {
            AddError("At least one weight part is required.");
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        var parts = new List<ImageModelPartRequest>(req.Parts.Count);
        foreach (var part in req.Parts)
        {
            if (!Enum.TryParse<ImageModelPartRole>(part.Role, ignoreCase: true, out var role))
            {
                AddError($"The part role '{part.Role}' is not recognized.");
                await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
                return;
            }

            if (string.IsNullOrWhiteSpace(part.FileName))
            {
                AddError("Each weight part requires a file name.");
                await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
                return;
            }

            parts.Add(new ImageModelPartRequest
            {
                Role = role,
                FileName = part.FileName.Trim(),
                Sha256 = string.IsNullOrWhiteSpace(part.Sha256) ? null : part.Sha256.Trim()
            });
        }

        if (parts.TrueForAll(static p => p.Role != ImageModelPartRole.Diffusion))
        {
            AddError("The file-set must include a diffusion part.");
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        var request = new ImageModelRequest
        {
            ModelName = req.ModelName.Trim(),
            RepoId = req.RepoId.Trim(),
            Family = family,
            Kind = kind,
            Parts = parts,
            Revision = string.IsNullOrWhiteSpace(req.Revision) ? null : req.Revision.Trim()
        };

        // Fire-and-forget: the download outlives this request. The detached task owns its own logging; the request token
        // is NOT captured (it is cancelled the instant the 202 is written).
        _ = RunDownloadDetachedAsync(request);

        await Send.ResultAsync(Results.Accepted(uri: null, new StartImageModelDownloadResponse
        {
            ModelName = request.ModelName,
            Accepted = true
        })).ConfigureAwait(false);
    }

    // Detached, self-contained download run: owns its own CTS + logging, swallows failures (logged) so a background pull
    // never surfaces as an unobserved task fault. Progress is dropped (no coordinator yet — follow-up); presence is
    // observed via the models list. The store is a singleton, so capturing it past the request scope is safe.
    private async Task RunDownloadDetachedAsync(ImageModelRequest request)
    {
        using var cts = new CancellationTokenSource();
        try
        {
            await _modelStore.EnsureModelAsync(request, progress: null, cts.Token).ConfigureAwait(false);
            _logger.LogInformation("Image model download completed for {ModelName}.", request.ModelName);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Image model download cancelled for {ModelName}.", request.ModelName);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Image model download failed for {ModelName}.", request.ModelName);
        }
    }
}

namespace XE_Local_AI_Engine.Client.Endpoints.Images.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Images;
using XE_Local_AI_Engine.Providers.Abstractions.Image;

/// <summary>
///     FastEndpoints handler that begins an image-model file-set download (POST images/models/downloads). It validates
///     the requested file-set, hands it to <see cref="IImageModelDownloadCoordinator" /> (which owns the detached
///     transfer plus its status registry), and returns 202 immediately. The download's outcome — including failure — is
///     observable via <c>GET images/models/downloads</c>; presence of the finished model surfaces via
///     <c>GET images/models</c>. No path/token is accepted or returned. Operator-gated.
/// </summary>
public sealed class StartImageModelDownloadEndpoint(IImageModelDownloadCoordinator downloadCoordinator)
    : Endpoint<StartImageModelDownloadRequest, StartImageModelDownloadResponse>
{
    private readonly IImageModelDownloadCoordinator _downloadCoordinator = downloadCoordinator ?? throw new ArgumentNullException(nameof(downloadCoordinator));

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
                Sha256 = string.IsNullOrWhiteSpace(part.Sha256) ? null : part.Sha256.Trim(),
                RepoId = string.IsNullOrWhiteSpace(part.RepoId) ? null : part.RepoId.Trim(),
                // A non-positive size is treated as "unknown" rather than accepted: it would poison the set total and
                // leave the disk pre-flight computing a smaller requirement than the transfer actually needs.
                SizeBytes = part.SizeBytes is > 0 ? part.SizeBytes : null
            });
        }

        // File-set shape (diffusion part present, one file per role) is owned by ImageModelFileSetRules next to the
        // download coordinator, because the rules come from what the launch-argument builder can emit.
        var fileSetError = ImageModelFileSetRules.Validate(parts);
        if (fileSetError is not null)
        {
            AddError(fileSetError);
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

        // The coordinator owns the detached transfer and records its terminal phase, so a failure is reported rather
        // than logged and forgotten. The request token is deliberately not involved — it is cancelled the instant the
        // 202 is written, while the download outlives this request.
        var ticket = _downloadCoordinator.Start(request);

        await Send.ResultAsync(Results.Accepted(uri: null, new StartImageModelDownloadResponse
        {
            ModelName = ticket.ModelName,
            Accepted = true,
            AlreadyInFlight = ticket.AlreadyInFlight
        })).ConfigureAwait(false);
    }
}

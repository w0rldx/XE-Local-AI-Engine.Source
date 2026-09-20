namespace XE_Local_AI_Engine.Client.Endpoints.Images.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Images.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Images;

/// <summary>
///     Begins an image-model file-set download, answering 202 immediately. Operator-gated.
/// </summary>
/// <remarks>
///     It validates the requested file-set and hands it to <see cref="IImageModelDownloadCoordinator" />, which owns
///     the detached transfer and its status registry. The outcome, failure included, is observable through
///     <c>GET images/models/downloads</c>, and the finished model through <c>GET images/models</c>. No path or token
///     is accepted or returned.
/// </remarks>
public sealed class StartImageModelDownloadEndpoint : Endpoint<StartImageModelDownloadRequest, StartImageModelDownloadResponse>
{
    private readonly IImageModelDownloadCoordinator _downloadCoordinator;

    public StartImageModelDownloadEndpoint(IImageModelDownloadCoordinator downloadCoordinator)
    {
        ArgumentNullException.ThrowIfNull(downloadCoordinator);
        _downloadCoordinator = downloadCoordinator;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Images.ModelDownloads);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(StartImageModelDownloadRequest req, CancellationToken ct)
    {
        var validation = StartImageModelDownloadWireValidator.Validate(req);
        if (!validation.IsValid)
        {
            AddError(validation.Error!);
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        var request = StartImageModelDownloadRequestMapper.ToServiceRequest(validation.Values!);

        // File-set shape (diffusion part present, one file per role) is owned by ImageModelFileSetRules next to the
        // download coordinator, because the rules come from what the launch-argument builder can emit.
        var fileSetError = ImageModelFileSetRules.Validate(request.Parts);
        if (fileSetError is not null)
        {
            AddError(fileSetError);
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        // The coordinator owns the detached transfer and records its terminal phase, so a failure is reported rather than logged and forgotten. The request token is
        // deliberately not involved: it is cancelled the instant the 202 is written, while the download outlives this request.
        var ticket = _downloadCoordinator.Start(request);

        await Send.ResultAsync(Results.Accepted(uri: null, new StartImageModelDownloadResponse
        {
            ModelName = ticket.ModelName,
            Accepted = true,
            AlreadyInFlight = ticket.AlreadyInFlight
        }));
    }
}

namespace XE_Local_AI_Engine.Client.Endpoints.Images.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Images;

/// <summary>
///     FastEndpoints handler cancelling an in-flight image-model weight download
///     (POST images/models/downloads/cancel). Mirrors <c>POST model-fit/gguf/downloads/cancel</c>.
/// </summary>
/// <remarks>
///     Idempotent by design: cancelling a download that already finished (or never started) is a 200 with
///     <c>Cancelled=false</c>, not an error — the operator clicked a button on a row that had just completed, which is a
///     race, not a mistake. Cancellation is cooperative and deliberately leaves the partial <c>.part</c> file on disk so
///     a later attempt resumes from it.
/// </remarks>
public sealed class CancelImageModelDownloadEndpoint(IImageModelDownloadCoordinator downloadCoordinator)
    : Endpoint<CancelImageModelDownloadRequest, CancelImageModelDownloadResponse>
{
    private readonly IImageModelDownloadCoordinator _downloadCoordinator = downloadCoordinator ?? throw new ArgumentNullException(nameof(downloadCoordinator));

    public override void Configure()
    {
        Post(LocalApiRoutes.Images.ModelDownloadCancel);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancelImageModelDownloadRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        if (string.IsNullOrWhiteSpace(req.ModelName))
        {
            AddError("A model name is required.");
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        var cancelled = _downloadCoordinator.Cancel(req.ModelName.Trim());

        await Send.OkAsync(new CancelImageModelDownloadResponse
        {
            ModelName = req.ModelName.Trim(),
            Cancelled = cancelled
        }, ct).ConfigureAwait(false);
    }
}

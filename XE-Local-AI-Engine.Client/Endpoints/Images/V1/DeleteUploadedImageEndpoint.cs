namespace XE_Local_AI_Engine.Client.Endpoints.Images.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Images;

/// <summary>
///     Deletes one uploaded image, its row and then its encrypted blob (DELETE images/uploads/{imageId}). Operator-gated.
/// </summary>
/// <remarks>
///     404 when the id is unknown or names a job-produced image, which only its job's delete removes. A job that still
///     references the upload is not blocked: it fails with "The source image no longer exists." if it runs later.
/// </remarks>
public sealed class DeleteUploadedImageEndpoint : Endpoint<UploadedImageRouteRequest>
{
    private readonly UploadedImageService _uploads;

    public DeleteUploadedImageEndpoint(UploadedImageService uploads)
    {
        ArgumentNullException.ThrowIfNull(uploads);
        _uploads = uploads;
    }

    public override void Configure()
    {
        Delete(LocalApiRoutes.Images.UploadById);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces(StatusCodes.Status204NoContent)
                                      .ProducesProblem(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(UploadedImageRouteRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        if (!await _uploads.DeleteAsync(req.ImageId, ct))
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.NoContentAsync(ct);
    }
}

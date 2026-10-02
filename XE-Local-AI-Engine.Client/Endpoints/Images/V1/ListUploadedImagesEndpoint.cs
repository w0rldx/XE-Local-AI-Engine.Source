namespace XE_Local_AI_Engine.Client.Endpoints.Images.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Images.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Images.V1.Validators;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Images;

/// <summary>Lists the operator-uploaded edit sources, newest first (GET images/uploads). Operator-gated.</summary>
public sealed class ListUploadedImagesEndpoint : Endpoint<ListUploadedImagesRequest, UploadedImageListResponse>
{
    private const int DefaultLimit = 100;

    private readonly UploadedImageService _uploads;

    public ListUploadedImagesEndpoint(UploadedImageService uploads)
    {
        ArgumentNullException.ThrowIfNull(uploads);
        _uploads = uploads;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Images.Uploads);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces<UploadedImageListResponse>(StatusCodes.Status200OK));
    }

    public override async Task HandleAsync(ListUploadedImagesRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var page = await _uploads.ListAsync(Math.Clamp(req.Limit ?? DefaultLimit, min: 1, ListUploadedImagesRequestValidator.MaxLimit),
            Math.Max(req.Offset ?? 0, val2: 0),
            ct);
        await Send.OkAsync(new UploadedImageListResponse
            {
                Items = [.. page.Items.Select(static row => row.ToUploadedResponse())],
                TotalCount = page.TotalCount
            },
            ct);
    }
}

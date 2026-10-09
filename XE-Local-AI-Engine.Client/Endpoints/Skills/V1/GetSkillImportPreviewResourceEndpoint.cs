namespace XE_Local_AI_Engine.Client.Endpoints.Skills.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Skills.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     Returns one bundled file, content included, from a live import preview, so the operator can read it before the
///     commit. The preview report itself never carries resource content.
/// </summary>
/// <remarks>
///     Reads the cached payload the commit would persist, so what is shown is what lands. The token is not consumed; an
///     expired or committed preview answers 404 and the operator previews the source again.
/// </remarks>
public sealed class GetSkillImportPreviewResourceEndpoint : Endpoint<GetSkillImportPreviewResourceRequest, SkillResourceResponse>
{
    private const string NotFoundMessage = "The import preview has expired, was already used, or holds no such file. Preview the source again.";

    private readonly ISkillImportService _importService;

    public GetSkillImportPreviewResourceEndpoint(ISkillImportService importService)
    {
        ArgumentNullException.ThrowIfNull(importService);
        _importService = importService;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Skills.ImportPreviewResource);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Produces<SkillResourceResponse>(StatusCodes.Status200OK)
                               .ProducesProblemFE(StatusCodes.Status400BadRequest)
                               .ProducesProblemFE(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(GetSkillImportPreviewResourceRequest req, CancellationToken ct)
    {
        var name = SkillResourceRouteName.DecodeAndValidate(req.ResourceName);
        if (name is null)
        {
            AddError("The resource name is invalid.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        var resource = _importService.FindPreviewResource(req.Token, req.SkillName ?? string.Empty, name);
        if (resource is null)
        {
            AddError(NotFoundMessage);
            await Send.ErrorsAsync(StatusCodes.Status404NotFound, ct);
            return;
        }

        await Send.OkAsync(resource.ToResponse(), ct);
    }
}

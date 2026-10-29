namespace XE_Local_AI_Engine.Client.Endpoints.Training.BaseArtifacts.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Training.BaseArtifacts.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Training.BaseArtifacts;

/// <summary>
///     The licensing metadata the run wizard's confirmation step reads. Always keyed on the base checkpoint repository
///     recorded on the artifact — never on a GGUF quant repository derived from it.
/// </summary>
public sealed class GetBaseArtifactLicenseEndpoint : Endpoint<BaseArtifactByIdRequest, BaseArtifactLicenseResponse>
{
    private readonly IBaseArtifactService _baseArtifactService;

    public GetBaseArtifactLicenseEndpoint(IBaseArtifactService baseArtifactService)
    {
        _baseArtifactService = baseArtifactService;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Training.BaseArtifactLicense);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Produces<BaseArtifactLicenseResponse>(StatusCodes.Status200OK)
                               .Produces(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(BaseArtifactByIdRequest request, CancellationToken ct)
    {
        var license = await _baseArtifactService.GetLicenseAsync(request.ArtifactId, ct);
        if (license is null)
        {
            // A known artifact with no license recorded yet: the metadata is written with the terminal Ready state, so a
            // running download has none. Not a missing id, so this stays the bodyless 404 the wizard polls past.
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(license.ToResponse(), ct);
    }
}

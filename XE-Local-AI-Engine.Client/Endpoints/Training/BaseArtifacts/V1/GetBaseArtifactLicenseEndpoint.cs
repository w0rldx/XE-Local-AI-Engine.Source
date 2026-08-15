namespace XE_Local_AI_Engine.Client.Endpoints.Training.BaseArtifacts.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Training.BaseArtifacts.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Training.BaseArtifacts;

/// <summary>
///     The licensing metadata the run wizard's confirmation step reads. Always keyed on the base checkpoint repository
///     recorded on the artifact — never on a GGUF quant repository derived from it (locked decision 8).
/// </summary>
public sealed class GetBaseArtifactLicenseEndpoint(IBaseArtifactService baseArtifactService)
    : Endpoint<BaseArtifactByIdRequest, BaseArtifactLicenseResponse>
{
    public override void Configure()
    {
        Get(LocalApiRoutes.Training.BaseArtifactLicense);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Produces<BaseArtifactLicenseResponse>(StatusCodes.Status200OK)
                               .ProducesProblemFE(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(BaseArtifactByIdRequest request, CancellationToken ct)
    {
        var license = await baseArtifactService.GetLicenseAsync(request.ArtifactId, ct).ConfigureAwait(false);
        if (license is null)
        {
            // Also the answer while a download is still running: the metadata is written with the terminal Ready state.
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        await Send.OkAsync(license.ToResponse(), ct).ConfigureAwait(false);
    }
}

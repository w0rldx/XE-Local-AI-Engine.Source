namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ModelFit;

public sealed class UpdateLlamaCppRuntimeEndpoint(
    ILlamaCppRuntimeAdministrationService administrationService)
    : Endpoint<UpdateLlamaCppRuntimeRequest, LlamaCppVersionResponse>
{
    private readonly ILlamaCppRuntimeAdministrationService _administrationService =
        administrationService ?? throw new ArgumentNullException(nameof(administrationService));

    public override void Configure()
    {
        Post(LocalApiRoutes.ModelFit.LlamaCppUpdate);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Produces<LlamaCppVersionResponse>(StatusCodes.Status200OK)
                               .ProducesProblemFE(StatusCodes.Status400BadRequest)
                               .Produces<LlamaCppUpdateBlockedResponse>(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(UpdateLlamaCppRuntimeRequest req, CancellationToken ct)
    {
        if (req.Variant is not null && ModelFitMapper.TryParseVariant(req.Variant) is null)
        {
            AddError(r => r.Variant, "Variant is not supported.");
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        var variant = req.Variant is null ? null : ModelFitMapper.TryParseVariant(req.Variant);
        var result = await _administrationService.InstallAsync(req.Tag, variant, ct).ConfigureAwait(false);
        if (result.Succeeded)
        {
            await Send.OkAsync(result.Binary!.ToResponse(result.RecommendedTag!), ct).ConfigureAwait(false);
            return;
        }

        if (result.Failure == LlamaCppRuntimeAdministrationFailure.Busy)
        {
            await Send.ResultAsync(Results.Conflict(new LlamaCppUpdateBlockedResponse
            {
                RunningProcessCount = result.RunningProcessCount,
                Message = result.DisplayMessage!
            })).ConfigureAwait(false);
            return;
        }

        AddError(result.DisplayMessage ?? "The llama.cpp runtime could not be installed.");
        await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
    }
}

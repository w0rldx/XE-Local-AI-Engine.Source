namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ModelFit;

public sealed class EnsureLlamaCppBinaryEndpoint(
    ILlamaCppRuntimeAdministrationService administrationService)
    : Endpoint<EnsureLlamaCppBinaryRequest, LlamaCppVersionResponse>
{
    private readonly ILlamaCppRuntimeAdministrationService _administrationService =
        administrationService ?? throw new ArgumentNullException(nameof(administrationService));

    public override void Configure()
    {
        Post(LocalApiRoutes.ModelFit.LlamaCppVersion);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Produces<LlamaCppVersionResponse>(StatusCodes.Status200OK)
                               .ProducesProblemFE(StatusCodes.Status400BadRequest)
                               .Produces<LlamaCppUpdateBlockedResponse>(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(EnsureLlamaCppBinaryRequest req, CancellationToken ct)
    {
        if (ModelFitMapper.TryParseVariant(req.Variant) is not { } variant)
        {
            AddError("Variant is not supported.");
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        var result = await _administrationService.EnsureAsync(variant, ct).ConfigureAwait(false);
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

        AddError(result.DisplayMessage ?? "The llama.cpp runtime could not be acquired.");
        await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
    }
}

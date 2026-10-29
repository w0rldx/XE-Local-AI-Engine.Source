namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using System.Diagnostics;
using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ModelFit;

public sealed class EnsureLlamaCppBinaryEndpoint : Endpoint<EnsureLlamaCppBinaryRequest, LlamaCppVersionResponse>
{
    private readonly ILlamaCppRuntimeAdministrationService _administrationService;

    public EnsureLlamaCppBinaryEndpoint(ILlamaCppRuntimeAdministrationService administrationService)
    {
        ArgumentNullException.ThrowIfNull(administrationService);
        _administrationService = administrationService;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.ModelFit.LlamaCppVersion);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Produces<LlamaCppVersionResponse>(StatusCodes.Status200OK)
                               .ProducesProblemDetails(StatusCodes.Status400BadRequest)
                               .Produces<LlamaCppUpdateBlockedResponse>(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(EnsureLlamaCppBinaryRequest req, CancellationToken ct)
    {
        if (ModelFitMapper.ParseVariantOrNull(req.Variant) is not { } variant)
        {
            AddError("Variant is not supported.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        var result = await _administrationService.EnsureAsync(variant, ct);
        switch (result)
        {
            case LlamaCppRuntimeMutationResult.Succeeded succeeded:
                await Send.OkAsync(succeeded.Binary.ToResponse(succeeded.RecommendedTag), ct);
                return;
            case LlamaCppRuntimeMutationResult.Rejected { Failure: LlamaCppRuntimeAdministrationFailure.Busy } busy:
                await Send.ResultAsync(Results.Conflict(new LlamaCppUpdateBlockedResponse
                {
                    RunningProcessCount = busy.RunningProcessCount,
                    Message = busy.DisplayMessage
                }));
                return;
            case LlamaCppRuntimeMutationResult.Rejected rejected:
                AddError(rejected.DisplayMessage);
                break;
            default:
                throw new UnreachableException($"Unknown llama.cpp runtime result '{result}'.");
        }

        await Send.ErrorsAsync(cancellation: ct);
    }
}

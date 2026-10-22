namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ModelFit;

/// <summary>
///     Drops an operator-pinned llama.cpp variant so the next spawn follows automatic selection again, answering the
///     refreshed runtime status. A recorded source build is left as it is and the answer is the unchanged status.
/// </summary>
public sealed class UseAutomaticLlamaCppVariantEndpoint : EndpointWithoutRequest<LlamaCppRuntimeStatusResponse>
{
    private readonly ILlamaCppRuntimeAdministrationService _administrationService;

    public UseAutomaticLlamaCppVariantEndpoint(ILlamaCppRuntimeAdministrationService administrationService)
    {
        ArgumentNullException.ThrowIfNull(administrationService);
        _administrationService = administrationService;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.ModelFit.LlamaCppVariantAutomatic);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Produces<LlamaCppRuntimeStatusResponse>(StatusCodes.Status200OK)
                               .Produces<LlamaCppUpdateBlockedResponse>(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var refusal = await _administrationService.UseAutomaticVariantAsync(ct);
        var status = await _administrationService.GetStatusAsync(cancellationToken: ct);
        if (refusal is not null)
        {
            await Send.ResultAsync(Results.Conflict(new LlamaCppUpdateBlockedResponse
            {
                RunningProcessCount = status.RunningProcessCount,
                Message = refusal
            }));
            return;
        }

        await Send.OkAsync(status.ToRuntimeStatusResponse(), ct);
    }
}

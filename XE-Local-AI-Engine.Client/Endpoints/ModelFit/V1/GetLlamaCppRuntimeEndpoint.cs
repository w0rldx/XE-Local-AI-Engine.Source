namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ModelFit;

public sealed class GetLlamaCppRuntimeEndpoint(
    ILlamaCppRuntimeAdministrationService administrationService)
    : Endpoint<GetLlamaCppRuntimeRequest, LlamaCppRuntimeStatusResponse>
{
    private readonly ILlamaCppRuntimeAdministrationService _administrationService =
        administrationService ?? throw new ArgumentNullException(nameof(administrationService));

    public override void Configure()
    {
        Get(LocalApiRoutes.ModelFit.LlamaCppRuntime);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(GetLlamaCppRuntimeRequest req, CancellationToken ct)
    {
        var status = await _administrationService.GetStatusAsync(req.Refresh ?? false, ct).ConfigureAwait(false);
        await Send.OkAsync(status.ToRuntimeStatusResponse(), ct).ConfigureAwait(false);
    }
}

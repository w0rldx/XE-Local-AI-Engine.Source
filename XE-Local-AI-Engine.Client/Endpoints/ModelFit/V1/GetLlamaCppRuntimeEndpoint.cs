namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ModelFit;

public sealed class GetLlamaCppRuntimeEndpoint : Endpoint<GetLlamaCppRuntimeRequest, LlamaCppRuntimeStatusResponse>
{
    private readonly ILlamaCppRuntimeAdministrationService _administrationService;

    public GetLlamaCppRuntimeEndpoint(ILlamaCppRuntimeAdministrationService administrationService)
    {
        ArgumentNullException.ThrowIfNull(administrationService);
        _administrationService = administrationService;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.ModelFit.LlamaCppRuntime);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(GetLlamaCppRuntimeRequest req, CancellationToken ct)
    {
        var status = await _administrationService.GetStatusAsync(req.Refresh ?? false, ct);
        await Send.OkAsync(status.ToRuntimeStatusResponse(), ct);
    }
}

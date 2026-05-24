namespace XE_Local_AI_Engine.Client.Endpoints.RuntimeManager.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Manager;

public sealed class GetRuntimeManagerStatusEndpoint(IHostAgentManagerService managerService) : EndpointWithoutRequest<RuntimeManagerStatusResponse>
{
    private readonly IHostAgentManagerService _managerService = managerService ?? throw new ArgumentNullException(nameof(managerService));

    public override void Configure()
    {
        Get(LocalApiRoutes.RuntimeManager.Status);
        Policies(LocalOperatorAuthorization.OperatorPolicy);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var snapshot = await _managerService.LoadSnapshotAsync(ct).ConfigureAwait(false);
        await Send.OkAsync(snapshot.ToResponse(), ct).ConfigureAwait(false);
    }
}

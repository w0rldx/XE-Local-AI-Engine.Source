namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ModelFit;

public sealed class GetGgufImportsEndpoint(IGgufImportTransactionCoordinator coordinator)
    : EndpointWithoutRequest<ListGgufImportsResponse>
{
    private readonly IGgufImportTransactionCoordinator _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    public override void Configure()
    {
        Get(LocalApiRoutes.ModelFit.Imports);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        await Send.OkAsync(new ListGgufImportsResponse
        {
            Items = _coordinator.ListStatuses().Select(GgufImportEndpointSupport.Map).ToArray()
        }, ct).ConfigureAwait(false);
    }
}

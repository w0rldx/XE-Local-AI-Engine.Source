namespace XE_Local_AI_Engine.Client.Endpoints.Connection.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Connection.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Connection;

public sealed class DisableAutoConnectEndpoint : EndpointWithoutRequest<ConnectionStatusResponse>
{
    private readonly IConnectionControlService _connectionControlService;

    public DisableAutoConnectEndpoint(IConnectionControlService connectionControlService)
    {
        ArgumentNullException.ThrowIfNull(connectionControlService);
        _connectionControlService = connectionControlService;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Connection.DisableAutoConnect);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var status = await _connectionControlService.SetAutoConnectAsync(enabled: false, ct);
        await Send.OkAsync(status.ToResponse(), ct);
    }
}

namespace XE_Local_AI_Engine.Client.Endpoints.Connection.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Connection.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Connection;

/// <summary>
///     FastEndpoints handler for the enable auto connect local API operation.
/// </summary>
public sealed class EnableAutoConnectEndpoint(IConnectionControlService connectionControlService) : EndpointWithoutRequest<ConnectionStatusResponse>
{
    private readonly IConnectionControlService _connectionControlService = connectionControlService ?? throw new ArgumentNullException(nameof(connectionControlService));

    public override void Configure()
    {
        Post(LocalApiRoutes.Connection.EnableAutoConnect);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var status = await _connectionControlService.SetAutoConnectAsync(true, ct).ConfigureAwait(false);
        await Send.OkAsync(status.ToResponse(), ct).ConfigureAwait(false);
    }
}

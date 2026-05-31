namespace XE_Local_AI_Engine.Client.Endpoints.Connection.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Connection.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Connection;

/// <summary>
///     FastEndpoints handler for the connect connection local API operation.
/// </summary>
public sealed class ConnectConnectionEndpoint(IConnectionControlService connectionControlService) : EndpointWithoutRequest<ConnectionStatusResponse>
{
    private readonly IConnectionControlService _connectionControlService = connectionControlService ?? throw new ArgumentNullException(nameof(connectionControlService));

    public override void Configure()
    {
        Post(LocalApiRoutes.Connection.Connect);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        try
        {
            var status = await _connectionControlService.ConnectAsync(ct).ConfigureAwait(false);
            await Send.OkAsync(status.ToResponse(), ct).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
        }
    }
}

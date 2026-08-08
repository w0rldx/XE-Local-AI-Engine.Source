namespace XE_Local_AI_Engine.Client.Endpoints.Connection.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Connection.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Connection;

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
        // No try/catch: connection failures flow to the global exception handlers. A not-paired / token-expired node
        // is mapped to a 409 by ConflictExceptionHandler (with its user-safe message); any other fault becomes a clean
        // 500 ProblemDetails via DefaultExceptionHandler. The previous catch-all flattened every fault into a 400 and
        // leaked the raw exception message to the client regardless of environment.
        var status = await _connectionControlService.ConnectAsync(ct).ConfigureAwait(false);
        await Send.OkAsync(status.ToResponse(), ct).ConfigureAwait(false);
    }
}

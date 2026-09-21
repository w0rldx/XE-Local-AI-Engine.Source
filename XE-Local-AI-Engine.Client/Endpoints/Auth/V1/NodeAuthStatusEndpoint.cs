namespace XE_Local_AI_Engine.Client.Endpoints.Auth.V1;

using FastEndpoints;
using FastEndpoints.Swagger;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;

public sealed class NodeAuthStatusEndpoint : EndpointWithoutRequest<NodeAuthStatusResponse>
{
    private readonly INodeAuthService _authService;

    public NodeAuthStatusEndpoint(INodeAuthService authService)
    {
        _authService = authService;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Auth.Status);
        AllowAnonymous();
        Description(static descriptor => descriptor.AutoTagOverride("Auth"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var status = await _authService.GetStatusAsync(User, ct);
        await Send.OkAsync(new NodeAuthStatusResponse
        {
            SetupRequired = status.SetupRequired,
            Authenticated = status.Authenticated
        }, ct);
    }
}

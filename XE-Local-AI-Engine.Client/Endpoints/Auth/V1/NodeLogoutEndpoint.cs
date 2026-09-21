namespace XE_Local_AI_Engine.Client.Endpoints.Auth.V1;

using FastEndpoints;
using FastEndpoints.Swagger;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;

public sealed class NodeLogoutEndpoint : EndpointWithoutRequest
{
    private readonly INodeAuthService _authService;

    public NodeLogoutEndpoint(INodeAuthService authService)
    {
        _authService = authService;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Auth.Logout);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(static descriptor => descriptor.AutoTagOverride("Auth"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        await _authService.RevokeRefreshTokensAsync(User, ct);
        NodeAuthCookie.ClearRefreshToken(HttpContext.Response);
        await Send.NoContentAsync(ct);
    }
}

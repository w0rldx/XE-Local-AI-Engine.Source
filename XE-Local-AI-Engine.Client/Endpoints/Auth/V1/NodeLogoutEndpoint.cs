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
        Summary(static summary =>
        {
            summary.Summary = "Signs the current session out.";
            summary.Description = "Ends only the caller's session (the access token's session, else the refresh token's) and its "
                                  + "refresh tokens; the session's access tokens are rejected at once. Other sessions stay signed in.";
        });
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var refreshToken = HttpContext.Request.Cookies[NodeAuthCookie.RefreshCookieName]
                           ?? HttpContext.Request.Headers[NodeRefreshEndpoint.RefreshTokenHeaderName].FirstOrDefault();
        await _authService.EndCurrentSessionAsync(User, refreshToken, ct);
        NodeAuthCookie.ClearRefreshToken(HttpContext.Response);
        await Send.NoContentAsync(ct);
    }
}

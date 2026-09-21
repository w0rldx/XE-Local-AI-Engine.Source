namespace XE_Local_AI_Engine.Client.Endpoints.Auth.V1;

using FastEndpoints;
using FastEndpoints.Swagger;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;

public sealed class NodeRefreshEndpoint : EndpointWithoutRequest<NodeAccessTokenResponse>
{
    private readonly INodeAuthService _authService;

    public NodeRefreshEndpoint(INodeAuthService authService)
    {
        _authService = authService;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Auth.Refresh);
        AllowAnonymous();
        Options(static options => options.RequireRateLimiting(NodeAuthRateLimits.AuthPolicy));
        Description(static descriptor => descriptor.AutoTagOverride("Auth"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var refreshToken = HttpContext.Request.Cookies[NodeAuthCookie.RefreshCookieName];
        var result = await _authService.RefreshAsync(refreshToken, ct);
        await NodeAuthEndpointSupport.SendTokenResultAsync(Send, result, ct);
    }
}

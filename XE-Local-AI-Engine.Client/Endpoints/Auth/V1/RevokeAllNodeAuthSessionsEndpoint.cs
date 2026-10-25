namespace XE_Local_AI_Engine.Client.Endpoints.Auth.V1;

using FastEndpoints;
using FastEndpoints.Swagger;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;

public sealed class RevokeAllNodeAuthSessionsEndpoint : EndpointWithoutRequest
{
    private readonly INodeAuthService _authService;

    public RevokeAllNodeAuthSessionsEndpoint(INodeAuthService authService)
    {
        _authService = authService;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Auth.RevokeAllSessions);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(static descriptor => descriptor.AutoTagOverride("Auth"));
        Summary(static summary =>
        {
            summary.Summary = "Signs every session of the caller out, the current one included.";
            summary.Description = "Ends every session and refresh token of the caller and rotates the security stamp, so every "
                                  + "access token already issued is rejected at once.";
        });
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        await _authService.RevokeAllSessionsAsync(User, ct);
        NodeAuthCookie.ClearRefreshToken(HttpContext.Response);
        await Send.NoContentAsync(ct);
    }
}

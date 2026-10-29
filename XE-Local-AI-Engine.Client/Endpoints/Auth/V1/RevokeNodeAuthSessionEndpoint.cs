namespace XE_Local_AI_Engine.Client.Endpoints.Auth.V1;

using FastEndpoints;
using FastEndpoints.Swagger;
using Microsoft.IdentityModel.JsonWebTokens;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;

public sealed class RevokeNodeAuthSessionEndpoint : Endpoint<NodeRevokeSessionRequest>
{
    private readonly INodeAuthService _authService;

    public RevokeNodeAuthSessionEndpoint(INodeAuthService authService)
    {
        _authService = authService;
    }

    public override void Configure()
    {
        Delete(LocalApiRoutes.Auth.SessionById);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(static descriptor => descriptor.Produces(StatusCodes.Status404NotFound)
                                                   .AutoTagOverride("Auth"));
        Summary(static summary =>
        {
            summary.Summary = "Signs one of the caller's sessions out.";
            summary.Description = "Ends the session and its refresh tokens; its access tokens are rejected at once. A session the "
                                  + "caller does not own answers 404.";
        });
    }

    public override async Task HandleAsync(NodeRevokeSessionRequest req, CancellationToken ct)
    {
        await _authService.RevokeSessionAsync(User, req.SessionId, ct);

        if (string.Equals(req.SessionId, User.FindFirst(JwtRegisteredClaimNames.Sid)?.Value, StringComparison.Ordinal))
        {
            NodeAuthCookie.ClearRefreshToken(HttpContext.Response);
        }

        await Send.NoContentAsync(ct);
    }
}

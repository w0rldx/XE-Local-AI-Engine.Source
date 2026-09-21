namespace XE_Local_AI_Engine.Client.Endpoints.Auth.V1;

using FastEndpoints;
using FastEndpoints.Swagger;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;

public sealed class NodeChangePasswordEndpoint : Endpoint<NodeChangePasswordRequest>
{
    private readonly INodeAuthService _authService;

    public NodeChangePasswordEndpoint(INodeAuthService authService)
    {
        _authService = authService;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Auth.ChangePassword);
        Policies(NodeAuthorizationPolicies.Operator);
        // Same throttle as setup/login/refresh. Being Operator-authorized bounds WHO can guess, not HOW OFTEN: the current password is verified here, so an already-signed-in
        // session could otherwise grind at it unbounded. The policy partitions on the peer address, so a change and the sign-in that must follow it share one 10/minute bucket.
        Options(static options => options.RequireRateLimiting(NodeAuthRateLimits.AuthPolicy));
        Description(static descriptor => descriptor.AutoTagOverride("Auth"));
    }

    public override async Task HandleAsync(NodeChangePasswordRequest req, CancellationToken ct)
    {
        var result = await _authService.ChangePasswordAsync(User, req.CurrentPassword, req.NewPassword, ct);
        if (!result.Succeeded)
        {
            await Send.ResultAsync(Results.BadRequest(new NodeAuthErrorResponse
            {
                Message = "Password change failed.",
                Errors = result.Errors
            }));
            return;
        }

        NodeAuthCookie.ClearRefreshToken(HttpContext.Response);
        await Send.NoContentAsync(ct);
    }
}

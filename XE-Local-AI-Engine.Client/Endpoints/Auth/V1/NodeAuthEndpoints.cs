namespace XE_Local_AI_Engine.Client.Endpoints.Auth.V1;

using FastEndpoints;
using FastEndpoints.Swagger;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;

public sealed class NodeAuthStatusEndpoint(INodeAuthService authService) : EndpointWithoutRequest<NodeAuthStatusResponse>
{
    public override void Configure()
    {
        Get(LocalApiRoutes.Auth.Status);
        AllowAnonymous();
        Description(static descriptor => descriptor.AutoTagOverride("Auth"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var status = await authService.GetStatusAsync(User, ct);
        await Send.OkAsync(new NodeAuthStatusResponse
        {
            SetupRequired = status.SetupRequired,
            Authenticated = status.Authenticated
        }, ct);
    }
}

public sealed class NodeSetupEndpoint(INodeAuthService authService) : Endpoint<NodeSetupRequest>
{
    public override void Configure()
    {
        Post(LocalApiRoutes.Auth.Setup);
        AllowAnonymous();
        Options(static options => options.RequireRateLimiting(NodeAuthRateLimits.AuthPolicy));
        Description(static descriptor => descriptor.AutoTagOverride("Auth"));
    }

    public override async Task HandleAsync(NodeSetupRequest req, CancellationToken ct)
    {
        var result = await authService.SetupAsync(req.Email, req.Password, ct);
        if (result.Succeeded)
        {
            await Send.NoContentAsync(ct);
            return;
        }

        if (result.AlreadyInitialized)
        {
            await Send.ResultAsync(Results.Conflict(new NodeAuthErrorResponse
            {
                Message = "Node auth is already configured."
            }));
            return;
        }

        await Send.ResultAsync(Results.BadRequest(new NodeAuthErrorResponse
        {
            Message = "Node auth setup failed.",
            Errors = result.Errors
        }));
    }
}

public sealed class NodeLoginEndpoint(INodeAuthService authService) : Endpoint<NodeLoginRequest, NodeAccessTokenResponse>
{
    public override void Configure()
    {
        Post(LocalApiRoutes.Auth.Login);
        AllowAnonymous();
        Options(static options => options.RequireRateLimiting(NodeAuthRateLimits.AuthPolicy));

        // The locked-out 401 carries a body where a wrong-password 401 carries none, so it has to be in the contract:
        // without it the generated client types the 401 as empty and the SPA has to hand-write the shape it reads.
        Description(static descriptor => descriptor.Produces<NodeLoginLockedOutResponse>(StatusCodes.Status401Unauthorized)
                                                   .AutoTagOverride("Auth"));
    }

    public override async Task HandleAsync(NodeLoginRequest req, CancellationToken ct)
    {
        var result = await authService.LoginAsync(req.Email, req.Password, ct);
        await NodeAuthEndpointSupport.SendTokenResultAsync(Send, result, ct);
    }
}

public sealed class NodeRefreshEndpoint(INodeAuthService authService) : EndpointWithoutRequest<NodeAccessTokenResponse>
{
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
        var result = await authService.RefreshAsync(refreshToken, ct);
        await NodeAuthEndpointSupport.SendTokenResultAsync(Send, result, ct);
    }
}

public sealed class NodeLogoutEndpoint(INodeAuthService authService) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Post(LocalApiRoutes.Auth.Logout);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(static descriptor => descriptor.AutoTagOverride("Auth"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        await authService.RevokeRefreshTokensAsync(User, ct);
        NodeAuthCookie.ClearRefreshToken(HttpContext.Response);
        await Send.NoContentAsync(ct);
    }
}

public sealed class NodeChangePasswordEndpoint(INodeAuthService authService) : Endpoint<NodeChangePasswordRequest>
{
    public override void Configure()
    {
        Post(LocalApiRoutes.Auth.ChangePassword);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(static descriptor => descriptor.AutoTagOverride("Auth"));
    }

    public override async Task HandleAsync(NodeChangePasswordRequest req, CancellationToken ct)
    {
        var result = await authService.ChangePasswordAsync(User, req.CurrentPassword, req.NewPassword, ct);
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

public sealed class NodeMeEndpoint(INodeAuthService authService) : EndpointWithoutRequest<NodeMeResponse>
{
    public override void Configure()
    {
        Get(LocalApiRoutes.Auth.Me);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(static descriptor => descriptor.AutoTagOverride("Auth"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var currentUser = await authService.GetCurrentUserAsync(User, ct);
        if (currentUser is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        await Send.OkAsync(new NodeMeResponse
        {
            UserName = currentUser.UserName,
            Roles = currentUser.Roles
        }, ct);
    }
}

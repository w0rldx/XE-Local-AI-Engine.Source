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

public sealed class NodeSetupEndpoint : Endpoint<NodeSetupRequest>
{
    private readonly INodeAuthService _authService;

    public NodeSetupEndpoint(INodeAuthService authService)
    {
        _authService = authService;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Auth.Setup);
        AllowAnonymous();
        Options(static options => options.RequireRateLimiting(NodeAuthRateLimits.AuthPolicy));
        Description(static descriptor => descriptor.AutoTagOverride("Auth"));
    }

    public override async Task HandleAsync(NodeSetupRequest req, CancellationToken ct)
    {
        var result = await _authService.SetupAsync(req.Email, req.Password, ct);
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

public sealed class NodeLoginEndpoint : Endpoint<NodeLoginRequest, NodeAccessTokenResponse>
{
    private readonly INodeAuthService _authService;

    public NodeLoginEndpoint(INodeAuthService authService)
    {
        _authService = authService;
    }

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
        var result = await _authService.LoginAsync(req.Email, req.Password, ct);
        await NodeAuthEndpointSupport.SendTokenResultAsync(Send, result, ct);
    }
}

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

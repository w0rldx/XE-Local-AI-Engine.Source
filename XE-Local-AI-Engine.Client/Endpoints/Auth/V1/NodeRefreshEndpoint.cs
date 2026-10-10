namespace XE_Local_AI_Engine.Client.Endpoints.Auth.V1;

using FastEndpoints;
using FastEndpoints.Swagger;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Hosting.Vault;
using XE_Local_AI_Engine.Client.Services.Auth;

public sealed class NodeRefreshEndpoint : EndpointWithoutRequest<NodeAccessTokenResponse>
{
    private readonly INodeAuthService _authService;
    private readonly TimeProvider _timeProvider;

    public NodeRefreshEndpoint(INodeAuthService authService, TimeProvider timeProvider)
    {
        _authService = authService;
        _timeProvider = timeProvider;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Auth.Refresh);
        AllowAnonymous();
        Options(static options => options.RequireRateLimiting(NodeAuthRateLimits.AuthPolicy));

        // A missing, expired or revoked cookie answers a bodyless 401 (see NodeAuthEndpointSupport). FastEndpoints drops its
        // auto-401 for an anonymous verb, so the contract states it here or the generated client never learns it exists.
        Description(static descriptor => descriptor.Produces(StatusCodes.Status401Unauthorized)
                                                   .AutoTagOverride("Auth"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var refreshToken = HttpContext.Request.Cookies[NodeAuthCookie.RefreshCookieName];
        var result = await _authService.RefreshAsync(refreshToken, ct);

        // A vault unlock's ticket (VaultUnlockHost) is spent by the first refresh that carries it, and trades for a
        // session only when the refresh cookie did not. The ticket is registered only after a pre-host password unlock.
        if (HttpContext.Request.Cookies[NodeAuthCookie.UnlockTicketCookieName] is { } unlockTicket)
        {
            NodeAuthCookie.ClearUnlockTicket(HttpContext.Response);
            var consumed = HttpContext.RequestServices.GetService<VaultUnlockTicket>()?.TryConsume(unlockTicket, _timeProvider.GetUtcNow()) == true;
            if (consumed && !result.Succeeded)
            {
                result = await _authService.IssueSessionAfterVaultUnlockAsync(ct);
            }
            else if (!result.Succeeded)
            {
                // A second tab replaying the spent ticket: its 401 must not clear the refresh cookie the first one just set.
                await Send.UnauthorizedAsync(ct);
                return;
            }
        }

        await NodeAuthEndpointSupport.SendTokenResultAsync(Send, result, ct);
    }
}

namespace XE_Local_AI_Engine.Client.Endpoints.Auth.V1;

using FastEndpoints;
using FastEndpoints.Swagger;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Hosting.Vault;
using XE_Local_AI_Engine.Client.Services.Auth;

public sealed class NodeRefreshEndpoint : EndpointWithoutRequest<NodeAccessTokenResponse>
{
    /// <summary>The refresh token of a body-mode (non-browser) client; read only when no <c>node_rt</c> cookie is sent.</summary>
    public const string RefreshTokenHeaderName = "X-XE-Refresh-Token";

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
        Options(static options => options.RequireRateLimiting(NodeAuthRateLimits.RefreshPolicy));

        // A missing, expired or revoked cookie answers a bodyless 401 (see NodeAuthEndpointSupport). FastEndpoints drops its
        // auto-401 for an anonymous verb, so the contract states it here or the generated client never learns it exists.
        Description(static descriptor => descriptor.Produces(StatusCodes.Status401Unauthorized)
                                                   .AutoTagOverride("Auth"));
        Summary(static summary =>
        {
            summary.Summary = "Rotates the refresh token and issues a new access token.";
            summary.Description = "Reads the node_rt cookie. Only when no cookie is sent, a non-browser client may send its refresh "
                                  + "token in the X-XE-Refresh-Token header; that answer carries the rotated refreshToken and "
                                  + "refreshTokenExpiresAtUtc in the body and never sets or clears a cookie.";
        });
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        // The cookie wins whenever it is sent, so a browser never reaches the header path; neither sent stays cookie mode.
        var bodyMode = !HttpContext.Request.Cookies.ContainsKey(NodeAuthCookie.RefreshCookieName)
                       && HttpContext.Request.Headers.ContainsKey(RefreshTokenHeaderName);
        var refreshToken = bodyMode
            ? HttpContext.Request.Headers[RefreshTokenHeaderName].ToString()
            : HttpContext.Request.Cookies[NodeAuthCookie.RefreshCookieName];
        var clientLabel = HttpContext.Request.Headers.UserAgent.ToString();
        var result = await _authService.RefreshAsync(refreshToken, clientLabel, ct);

        // A vault unlock's ticket (VaultUnlockHost) is spent by the first refresh that carries it, and trades for a
        // session only when the refresh cookie did not. The ticket is registered only after a pre-host password unlock.
        if (HttpContext.Request.Cookies[NodeAuthCookie.UnlockTicketCookieName] is { } unlockTicket)
        {
            NodeAuthCookie.ClearUnlockTicket(HttpContext.Response);
            var consumed = HttpContext.RequestServices.GetService<VaultUnlockTicket>()?.TryConsume(unlockTicket, _timeProvider.GetUtcNow()) == true;
            if (consumed && !result.Succeeded)
            {
                result = await _authService.IssueSessionAfterVaultUnlockAsync(clientLabel, ct);
            }
            else if (!result.Succeeded)
            {
                // A second tab replaying the spent ticket: its 401 must not clear the refresh cookie the first one just set.
                await Send.UnauthorizedAsync(ct);
                return;
            }
        }

        await NodeAuthEndpointSupport.SendTokenResultAsync(Send, result, bodyMode, ct);
    }
}

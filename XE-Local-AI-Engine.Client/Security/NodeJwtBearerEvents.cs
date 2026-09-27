namespace XE_Local_AI_Engine.Client.Security;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.JsonWebTokens;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     The JWT bearer events for the operator scheme, registered through <c>JwtBearerOptions.EventsType</c>: the SignalR
///     query-string token and the per-request security-stamp revocation check.
/// </summary>
public sealed class NodeJwtBearerEvents : JwtBearerEvents
{
    /// <summary>
    ///     Browsers cannot set an <c>Authorization</c> header on a WebSocket handshake, so a hub path under the local API
    ///     prefix reads the token from the <c>access_token</c> query parameter. Never on an ordinary REST route.
    /// </summary>
    public override Task MessageReceived(MessageReceivedContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var path = context.HttpContext.Request.Path;
        if (path.StartsWithSegments($"/{LocalApiRoutes.Prefix}", StringComparison.OrdinalIgnoreCase)
            && path.Value?.EndsWith("/hub", StringComparison.OrdinalIgnoreCase) == true)
        {
            var token = context.Request.Query["access_token"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(token))
            {
                context.Token = token;
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    ///     Stateless JWTs carry no revocation state, so enforce the user's current Identity security stamp here: a password
    ///     reset rotates it and must invalidate every token minted before the change. One indexed lookup per request.
    /// </summary>
    public override async Task TokenValidated(TokenValidatedContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var userId = context.Principal?.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            return;
        }

        // Fail CLOSED when the token carries no stamp: every token minted for a persisted user binds one, so an unstamped
        // but validly-signed token is a legacy token that must not outlive a reset, or a forgery. Never a bypass.
        var tokenStamp = context.Principal?.FindFirst(NodeAuthorizationPolicies.SecurityStampClaimType)?.Value;
        if (string.IsNullOrEmpty(tokenStamp))
        {
            context.Fail("Access token is missing its security stamp.");
            return;
        }

        // Resolved per validated token rather than injected: this singleton is activated on every authenticate call,
        // anonymous and static-file requests included, and only a validated token needs the stamp check.
        var stampCheck = context.HttpContext.RequestServices.GetRequiredService<NodeSecurityStampCheck>();
        if (!await stampCheck.IsCurrentAsync(userId, tokenStamp))
        {
            context.Fail("Access token security stamp is stale.");
        }
    }
}

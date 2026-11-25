namespace XE_Local_AI_Engine.Client.Security;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     The JWT bearer events for the operator scheme, registered through <c>JwtBearerOptions.EventsType</c>: the hub
///     socket's bearer subprotocol and the per-request security-stamp and session revocation checks.
/// </summary>
public sealed class NodeJwtBearerEvents : JwtBearerEvents
{
    /// <summary>The WebSocket subprotocol marker the SPA offers before its access token, and the one value the node echoes.</summary>
    public const string HubBearerProtocol = "xe.bearer";

    /// <summary>
    ///     Browsers cannot set an <c>Authorization</c> header on a WebSocket, so a genuine upgrade of a hub transport
    ///     endpoint without a <c>Bearer</c> header reads the token from <c>Sec-WebSocket-Protocol: xe.bearer, &lt;jwt&gt;</c>.
    /// </summary>
    /// <remarks>
    ///     Never on negotiate, a long poll, a send, a DELETE or a REST route, and never from the <c>access_token</c> query.
    ///     <c>UseWebSockets</c> runs before authentication so <c>IsWebSocketRequest</c> is known here.
    /// </remarks>
    public override Task MessageReceived(MessageReceivedContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var metadata = context.HttpContext.GetEndpoint()?.Metadata;
        // The handler reads no token from a bare "Bearer", so it stays a 401 here rather than falling back.
        var authorization = context.Request.Headers.Authorization.ToString().Trim();
        if (metadata?.GetMetadata<HubMetadata>() is null
            || metadata.GetMetadata<NegotiateMetadata>() is not null
            || !context.HttpContext.WebSockets.IsWebSocketRequest
            || authorization.Equals("Bearer", StringComparison.OrdinalIgnoreCase)
            || authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return Task.CompletedTask;
        }

        // Raw values, not GetCommaSeparatedValues, which unquotes and drops empty elements; field lines join with ','.
        if (context.Request.Headers.SecWebSocketProtocol.ToString().Split(',', StringSplitOptions.TrimEntries)
                is [HubBearerProtocol, var token]
            && token.Length > 0
            && !token.Contains('"', StringComparison.Ordinal))
        {
            context.Token = token;
        }

        return Task.CompletedTask;
    }

    /// <summary>Selects the bearer subprotocol when offered; <see langword="null" /> when not, so no header is sent.</summary>
    public static string? SelectHubSubProtocol(IList<string> requested)
    {
        ArgumentNullException.ThrowIfNull(requested);
        return requested.Contains(HubBearerProtocol) ? HubBearerProtocol : null;
    }

    /// <summary>
    ///     Stateless JWTs carry no revocation state, so enforce the user's current Identity security stamp here: a password
    ///     reset rotates it and must invalidate every token minted before the change. One indexed lookup per request.
    /// </summary>
    public override async Task TokenValidated(TokenValidatedContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Principal is null)
        {
            return;
        }

        // Resolved per validated token rather than injected: this singleton is activated on every authenticate call,
        // anonymous and static-file requests included, and only a validated token needs the stamp check.
        var stampCheck = context.HttpContext.RequestServices.GetRequiredService<NodeSecurityStampCheck>();
        if (await stampCheck.FindRevocationAsync(context.Principal, context.HttpContext.RequestAborted) is { } failure)
        {
            context.Fail(failure);
        }
    }
}

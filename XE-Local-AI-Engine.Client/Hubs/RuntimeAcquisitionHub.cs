namespace XE_Local_AI_Engine.Client.Hubs;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     Server-push hub for first-run llama.cpp runtime acquisition progress (GPU probe → download → verify → extract).
///     Clients connect and receive sanitized status events broadcast via <see cref="RuntimeAcquisitionEventPublisher" />
///     (<see cref="IHubContext{T}" />); there are no client-callable server methods. The acquisition-status GET stays for
///     the one-shot hydrate on mount — acquisition starts within seconds of boot, very likely before the React app has
///     authenticated and opened this connection, so the hub alone would never show the first install. Operator-gated like
///     the other local hubs.
/// </summary>
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = NodeAuthorizationPolicies.Operator)]
public sealed class RuntimeAcquisitionHub : Hub
{
}

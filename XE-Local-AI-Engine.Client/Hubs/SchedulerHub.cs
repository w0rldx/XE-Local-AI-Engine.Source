namespace XE_Local_AI_Engine.Client.Hubs;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     Server-push hub for scheduler lifecycle notifications. Clients connect and receive sanitized run/definition
///     events broadcast via <see cref="SchedulerEventPublisher" /> (<see cref="IHubContext{T}" />); there are no
///     client-callable server methods. Protected with the same operator policy as the other local hubs.
/// </summary>
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = NodeAuthorizationPolicies.Operator)]
public sealed class SchedulerHub : Hub
{
}

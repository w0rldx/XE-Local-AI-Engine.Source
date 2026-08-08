namespace XE_Local_AI_Engine.Client.Hubs;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     Server-push hub for in-app CUDA build progress. Clients connect and receive build phase + appended log line events
///     broadcast via <see cref="CudaBuildEventPublisher" /> (<see cref="IHubContext{T}" />); there are no client-callable
///     server methods. The status GET stays for the one-shot hydrate on mount. Operator-gated like the other local hubs.
/// </summary>
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = NodeAuthorizationPolicies.Operator)]
public sealed class CudaBuildHub : Hub
{
}

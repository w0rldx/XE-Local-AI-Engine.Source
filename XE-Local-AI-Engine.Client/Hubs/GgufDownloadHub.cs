namespace XE_Local_AI_Engine.Client.Hubs;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     Server-push hub for GGUF download progress. Clients connect and receive sanitized download-status events broadcast
///     via <see cref="GgufDownloadEventPublisher" /> (<see cref="IHubContext{T}" />); there are no client-callable server
///     methods. Replaces the per-second <c>GET model-fit/gguf/downloads</c> poll — the list endpoint stays for the
///     one-shot hydrate on mount. Protected with the same operator policy as the other local hubs.
/// </summary>
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = NodeAuthorizationPolicies.Operator)]
public sealed class GgufDownloadHub : Hub
{
}

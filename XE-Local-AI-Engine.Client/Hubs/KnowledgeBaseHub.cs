namespace XE_Local_AI_Engine.Client.Hubs;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     Server-push hub for knowledge-base indexing notifications. Clients connect and receive sanitized document
///     status-change events broadcast via <see cref="KnowledgeIndexingNotifier" /> (<see cref="IHubContext{T}" />); there
///     are no client-callable server methods. Protected with the same operator policy as the other local hubs because the
///     indexing stream reveals which documents exist and are being processed.
/// </summary>
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = NodeAuthorizationPolicies.Operator)]
public sealed class KnowledgeBaseHub : Hub
{
}

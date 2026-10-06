namespace XE_Local_AI_Engine.Client.Hubs;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     Server-push hub telling the header that runtime residency (running llama-server processes, image and whisper
///     daemons) changed.
/// </summary>
/// <remarks>
///     A change tick from <see cref="RuntimeResidencyChangePublisher" />, never a payload: the client invalidates
///     <c>GET model-fit/running</c> and <c>GET model-fit/runtime-residents</c>, which stay the only source of the rows.
///     No client-callable server methods. Operator-gated like the other local hubs.
/// </remarks>
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = NodeAuthorizationPolicies.Operator)]
public sealed class RuntimeResidencyHub : Hub
{
}

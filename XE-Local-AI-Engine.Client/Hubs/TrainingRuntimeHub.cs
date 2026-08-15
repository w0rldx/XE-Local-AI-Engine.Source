namespace XE_Local_AI_Engine.Client.Hubs;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     Push channel for training-runtime install phase and log changes. Deliberately empty: there is exactly one
///     machine-global runtime, so every Operator-authenticated client wants the same broadcast and there is nothing to
///     subscribe to. All push logic lives in <see cref="TrainingRuntimeEventPublisher" />.
/// </summary>
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = NodeAuthorizationPolicies.Operator)]
public sealed class TrainingRuntimeHub : Hub
{
}

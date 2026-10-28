namespace XE_Local_AI_Engine.Testing.FakeDocker.Endpoints;

using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;

/// <summary>
///     <c>GET /version</c>. Only the four fields <c>DockerDaemonIdentity</c> uses, under Docker's own names that
///     <c>VersionResponse</c> binds to: <c>ApiVersion</c> and <c>MinAPIVersion</c> differ in case on the daemon too.
/// </summary>
internal static class VersionEndpoint
{
    public static Task HandleAsync(HttpContext context, FakeDockerState state)
    {
        return FakeDockerEndpointMapper.WriteJsonAsync(context,
            new JsonObject
            {
                ["Version"] = state.ServerVersion,
                ["ApiVersion"] = state.ApiVersion,
                ["MinAPIVersion"] = state.MinimumApiVersion,
                ["Os"] = state.OperatingSystem
            });
    }
}

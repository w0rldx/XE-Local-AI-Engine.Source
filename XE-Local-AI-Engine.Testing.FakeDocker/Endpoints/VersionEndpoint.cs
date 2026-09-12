namespace XE_Local_AI_Engine.Testing.FakeDocker.Endpoints;

using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;

/// <summary>
///     <c>GET /version</c>. Only the four fields <c>DockerDaemonIdentity</c> is built from are emitted. The property
///     names are Docker's own and are what <c>VersionResponse</c> binds to — note <c>ApiVersion</c> and
///     <c>MinAPIVersion</c> capitalise differently, which is the daemon's inconsistency and not a typo here.
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

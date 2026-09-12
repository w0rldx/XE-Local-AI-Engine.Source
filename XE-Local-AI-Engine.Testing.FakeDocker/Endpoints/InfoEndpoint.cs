namespace XE_Local_AI_Engine.Testing.FakeDocker.Endpoints;

using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;

/// <summary>
///     <c>GET /info</c>. The installation id and the security options, and nothing else the client never reads.
///     <para>
///         The security options are rendered as the daemon renders them — comma-separated key/value groups whose
///         first part is <c>name=…</c>, with the seccomp entry carrying a profile after it. The client matches on the
///         <c>name=</c> part only, so an entry written as a bare word would silently never match.
///     </para>
/// </summary>
internal static class InfoEndpoint
{
    public static Task HandleAsync(HttpContext context, FakeDockerState state)
    {
        var securityOptions = new JsonArray();
        if (state.SupportsSeccomp)
        {
            securityOptions.Add("name=seccomp,profile=builtin");
        }

        if (state.Rootless)
        {
            securityOptions.Add("name=rootless");
        }

        return FakeDockerEndpointMapper.WriteJsonAsync(context,
            new JsonObject
            {
                ["ID"] = state.DaemonId,
                ["OSType"] = state.OperatingSystem,
                ["ServerVersion"] = state.ServerVersion,
                ["SecurityOptions"] = securityOptions
            });
    }
}

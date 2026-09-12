namespace XE_Local_AI_Engine.Testing.FakeDocker.Endpoints;

using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;

/// <summary>
///     <c>POST /exec/{id}/start</c> — the one hijacked route.
///     <para>
///         The client takes over the connection and reads Docker's framed stream off it directly, and it will only do
///         that for a response that is neither chunked nor content-length delimited. The three response headers
///         written here are what produce one: <c>Upgrade: tcp</c> is what makes the client treat the body as the raw
///         transport, and <c>Transfer-Encoding: identity</c> is what stops the host framing it underneath. Drop
///         either and the client refuses the hijack outright rather than mis-reading the bytes.
///     </para>
///     <para>
///         Read-only: the payload a caller sends on standard input is not modelled. The tests that need a genuinely
///         bidirectional stream stay against a real daemon.
///     </para>
/// </summary>
internal static class ExecStartEndpoint
{
    public static async Task HandleAsync(HttpContext context, FakeDockerState state, string id)
    {
        if (!state.ExecSessions.TryGetValue(id, out var session))
        {
            await FakeDockerEndpointMapper.WriteErrorAsync(context, StatusCodes.Status404NotFound, $"No such exec instance: {id}")
                                          .ConfigureAwait(false);
            return;
        }

        var outcome = state.ResolveExec(session.ContainerId, session.CommandLine);
        session.ExitCode = outcome.ExitCode;

        if (outcome.ExitCode == 0 && state.WritesThroughBindMounts)
        {
            TouchThroughBindMount(state, session);
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.Headers.Connection = "Upgrade";
        context.Response.Headers.Upgrade = "tcp";

        await FakeDockerEndpointMapper.WriteFramedAsync(context,
                                          [
                                              new FakeDockerLogFrame(FakeDockerStreamKind.StandardOutput, outcome.StandardOutput),
                                              new FakeDockerLogFrame(FakeDockerStreamKind.StandardError, outcome.StandardError)
                                          ],
                                          hijacked: true)
                                      .ConfigureAwait(false);
    }

    /// <summary>
    ///     Emulates the one wire effect a scripted fake cannot leave unmodelled: a <c>touch</c> of a path inside a
    ///     bind mount appears on the host side of that mount. Only <c>touch</c>, only one argument, and only under a
    ///     mount the create request declared — everything else stays a scripted outcome, because a fake that started
    ///     really running commands would stop being a fake.
    /// </summary>
    private static void TouchThroughBindMount(FakeDockerState state, FakeDockerExecSession session)
    {
        const string TouchPrefix = "touch ";

        if (!session.CommandLine.StartsWith(TouchPrefix, StringComparison.Ordinal)
            || !state.Containers.TryGetValue(session.ContainerId, out var container))
        {
            return;
        }

        var containerPath = session.CommandLine[TouchPrefix.Length..];
        if (containerPath.Length == 0 || containerPath.Contains(' ', StringComparison.Ordinal))
        {
            // More than one argument, so this is not the single-path probe the provider issues.
            return;
        }

        if (container.CreateRequest["HostConfig"]?["Mounts"] is not JsonArray mounts)
        {
            return;
        }

        foreach (var mount in mounts.OfType<JsonObject>())
        {
            if (mount["Target"]?.GetValue<string>() is not { } target
                || mount["Source"]?.GetValue<string>() is not { } source)
            {
                continue;
            }

            var prefix = target.EndsWith('/') ? target : target + "/";
            if (!containerPath.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var relative = containerPath[prefix.Length..].Replace('/', Path.DirectorySeparatorChar);
            var hostPath = Path.Combine(source, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(hostPath)!);
            File.WriteAllBytes(hostPath, []);
            return;
        }
    }
}

namespace XE_Local_AI_Engine.Testing.FakeDocker.Endpoints;

using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;

/// <summary>
///     <c>GET /exec/{id}/json</c>. The exit code is the only field the production client reads, and it is null until
///     the exec has been started — which is why it is recorded by the start route rather than guessed at create.
/// </summary>
internal static class ExecInspectEndpoint
{
    public static Task HandleAsync(HttpContext context, FakeDockerState state, string id)
    {
        if (!state.ExecSessions.TryGetValue(id, out var session))
        {
            return FakeDockerEndpointMapper.WriteErrorAsync(context, StatusCodes.Status404NotFound, $"No such exec instance: {id}");
        }

        var inspected = new JsonObject
        {
            ["ID"] = session.Id,
            ["ContainerID"] = session.ContainerId,
            ["Running"] = session.ExitCode is null
        };

        if (session.ExitCode is { } exitCode)
        {
            inspected["ExitCode"] = exitCode;
        }

        return FakeDockerEndpointMapper.WriteJsonAsync(context, inspected);
    }
}

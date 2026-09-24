namespace XE_Local_AI_Engine.Testing.FakeDocker.Endpoints;

using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;

/// <summary>
///     <c>POST /containers/{id}/exec</c>. The command is flattened to the space-joined line the scripting API keys
///     on — the same key the in-memory <c>FakeDockerRuntimeClient</c> already uses, so a test that scripts one
///     scripts both.
/// </summary>
internal static class ExecCreateEndpoint
{
    public static async Task HandleAsync(HttpContext context, FakeDockerState state, string id)
    {
        if (!state.Containers.ContainsKey(id))
        {
            await FakeDockerEndpointMapper.WriteErrorAsync(context, StatusCodes.Status404NotFound, $"No such container: {id}");
            return;
        }

        var body = await FakeDockerEndpointMapper.ReadJsonObjectAsync(context);
        var command = body?["Cmd"] is JsonArray cmd
            ? string.Join(' ', cmd.Select(argument => argument?.GetValue<string>() ?? string.Empty))
            : string.Empty;

        var session = new FakeDockerExecSession
        {
            Id = FakeDockerState.NewId(),
            ContainerId = id,
            CommandLine = command,
            WorkingDirectory = body?["WorkingDir"]?.GetValue<string>() ?? string.Empty
        };

        state.ExecSessions[session.Id] = session;

        await FakeDockerEndpointMapper.WriteJsonAsync(context,
            new JsonObject
            {
                ["Id"] = session.Id
            },
            StatusCodes.Status201Created);
    }
}

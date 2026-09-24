namespace XE_Local_AI_Engine.Testing.FakeDocker.Endpoints;

using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;

/// <summary>
///     <c>POST /networks/create</c>. A name that is already taken is a <c>409 Conflict</c> and nothing more — the
///     daemon does not say whose network it is, which is exactly why the production client answers the conflict with
///     an inspect and an ownership check rather than by reusing whatever holds the name.
/// </summary>
internal static class NetworkCreateEndpoint
{
    public static async Task HandleAsync(HttpContext context, FakeDockerState state)
    {
        var body = await FakeDockerEndpointMapper.ReadJsonObjectAsync(context);
        var name = body?["Name"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(name))
        {
            await FakeDockerEndpointMapper.WriteErrorAsync(context, StatusCodes.Status400BadRequest, "name is required");
            return;
        }

        if (state.Networks.ContainsKey(name))
        {
            await FakeDockerEndpointMapper
                .WriteErrorAsync(context, StatusCodes.Status409Conflict, $"network with name {name} already exists");
            return;
        }

        var network = new FakeDockerNetwork
        {
            Id = FakeDockerState.NewId(),
            Name = name,
            Driver = body?["Driver"]?.GetValue<string>() ?? "bridge",
            Internal = body?["Internal"]?.GetValue<bool>() ?? false
        };

        if (body?["Labels"] is JsonObject labels)
        {
            foreach (var (key, value) in labels)
            {
                network.Labels[key] = value?.GetValue<string>() ?? string.Empty;
            }
        }

        state.Networks[name] = network;

        await FakeDockerEndpointMapper.WriteJsonAsync(context,
            new JsonObject
            {
                ["Id"] = network.Id,
                ["Warning"] = string.Empty
            },
            StatusCodes.Status201Created);
    }
}

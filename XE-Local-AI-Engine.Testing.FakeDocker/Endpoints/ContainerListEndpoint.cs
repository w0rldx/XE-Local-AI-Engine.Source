namespace XE_Local_AI_Engine.Testing.FakeDocker.Endpoints;

using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;

/// <summary>
///     <c>GET /containers/json?all=1&amp;filters=</c>. The label filter arrives as the daemon's nested
///     <c>{"label":{"key=value":true}}</c> shape, and the status is rendered as the daemon's own prose — the
///     production client has no exit-code field on this response and parses the number back out of
///     <c>Exited (137) 3 minutes ago</c>, so prose that only looked plausible would defeat the parser it exists for.
/// </summary>
internal static class ContainerListEndpoint
{
    public static Task HandleAsync(HttpContext context, FakeDockerState state)
    {
        var required = FakeDockerEndpointMapper.ParseLabelFilter(context.Request.Query["filters"].ToString());

        var listed = new JsonArray();
        foreach (var container in state.Containers.Values)
        {
            var labels = container.CreateRequest["Labels"] as JsonObject;
            if (!Matches(labels, required))
            {
                continue;
            }

            listed.Add(new JsonObject
            {
                ["Id"] = container.Id,
                ["Names"] = new JsonArray("/" + container.Name),
                ["Image"] = container.CreateRequest["Image"]?.GetValue<string>() ?? string.Empty,
                ["Labels"] = labels?.DeepClone() ?? new JsonObject(),
                ["State"] = container.State,
                ["Status"] = Status(container),
                ["HostConfig"] = new JsonObject
                {
                    ["NetworkMode"] = container.CreateRequest["HostConfig"]?["NetworkMode"]?.GetValue<string>() ?? string.Empty
                }
            });
        }

        return FakeDockerEndpointMapper.WriteJsonAsync(context, listed);
    }

    private static bool Matches(JsonObject? labels, IReadOnlyDictionary<string, string> required)
    {
        foreach (var (key, value) in required)
        {
            if (labels?[key]?.GetValue<string>() is not { } actual || !string.Equals(actual, value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static string Status(FakeDockerContainer container)
    {
        return container.State switch
        {
            "running" => "Up 3 seconds",
            "exited" => string.Create(CultureInfo.InvariantCulture, $"Exited ({container.ExitCode}) 3 minutes ago"),
            _ => "Created"
        };
    }
}

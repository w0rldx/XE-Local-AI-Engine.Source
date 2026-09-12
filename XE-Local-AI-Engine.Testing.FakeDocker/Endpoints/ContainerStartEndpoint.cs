namespace XE_Local_AI_Engine.Testing.FakeDocker.Endpoints;

using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;

/// <summary>
///     <c>POST /containers/{id}/start</c>. Starting is what turns the requested port bindings into effective ones:
///     before it, <c>NetworkSettings.Ports</c> is empty and only <c>HostConfig.PortBindings</c> says anything, which
///     is the split the production inspect mapping reads two different fields for.
/// </summary>
internal static class ContainerStartEndpoint
{
    public static Task HandleAsync(HttpContext context, FakeDockerState state, string id)
    {
        if (!state.Containers.TryGetValue(id, out var container))
        {
            return FakeDockerEndpointMapper.WriteErrorAsync(context, StatusCodes.Status404NotFound, $"No such container: {id}");
        }

        container.Running = true;
        container.State = "running";
        container.StartedAtUtc = DateTimeOffset.UtcNow;
        container.FinishedAtUtc = null;

        if (container.CreateRequest["Healthcheck"] is JsonObject && container.HealthStatus is null)
        {
            // A container that declared a healthcheck is "starting" until the daemon's first probe lands. A test
            // that wants a verdict sets one; inventing "healthy" here would make every declared healthcheck pass.
            container.HealthStatus = "starting";
        }

        MaterialisePorts(state, container);

        context.Response.StatusCode = StatusCodes.Status204NoContent;
        return Task.CompletedTask;
    }

    private static void MaterialisePorts(FakeDockerState state, FakeDockerContainer container)
    {
        container.EffectivePorts.Clear();

        if (container.CreateRequest["HostConfig"]?["PortBindings"] is not JsonObject requested)
        {
            return;
        }

        foreach (var (portKey, bindings) in requested)
        {
            if (bindings is not JsonArray requestedBindings)
            {
                continue;
            }

            var effective = new JsonArray();
            foreach (var binding in requestedBindings.OfType<JsonObject>())
            {
                var hostPort = binding["HostPort"]?.GetValue<string>() ?? string.Empty;
                effective.Add(new JsonObject
                {
                    ["HostIp"] = binding["HostIp"]?.GetValue<string>() ?? string.Empty,
                    // An empty requested host port is the ask for a daemon-assigned one, so the fake assigns one out
                    // of the ephemeral range rather than echoing the blank back as if it were a port.
                    ["HostPort"] = hostPort.Length == 0
                        ? state.NextEphemeralPort().ToString(CultureInfo.InvariantCulture)
                        : hostPort
                });
            }

            container.EffectivePorts[portKey] = effective;
        }
    }
}

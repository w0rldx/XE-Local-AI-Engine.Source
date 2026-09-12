namespace XE_Local_AI_Engine.Testing.FakeDocker.Endpoints;

using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;

/// <summary>
///     <c>GET /containers/{id}/json</c>.
///     <para>
///         <c>Config</c> and <c>HostConfig</c> are the create request echoed back, split the way the daemon splits
///         it, rather than projected through a model of this fake's own. That is deliberate: the sandbox path never
///         sends a restart policy, extra hosts or a healthcheck and the application path never sends <c>Tmpfs</c>,
///         so anything this fake filled in would report a field a real daemon would have left at its zero value.
///     </para>
/// </summary>
internal static class ContainerInspectEndpoint
{
    /// <summary>The daemon's rendering of "never": the caller's mapper reads it back as null.</summary>
    private const string NeverTimestamp = "0001-01-01T00:00:00Z";

    public static Task HandleAsync(HttpContext context, FakeDockerState state, string id)
    {
        if (!state.Containers.TryGetValue(id, out var container))
        {
            return FakeDockerEndpointMapper.WriteErrorAsync(context, StatusCodes.Status404NotFound, $"No such container: {id}");
        }

        return FakeDockerEndpointMapper.WriteJsonAsync(context, Describe(container));
    }

    /// <summary>The inspect document for one container, shared with the routes that need to render a container.</summary>
    internal static JsonObject Describe(FakeDockerContainer container)
    {
        var config = (JsonObject)container.CreateRequest.DeepClone();
        config.Remove("HostConfig");
        config.Remove("NetworkingConfig");
        config.Remove("Name");

        var state = new JsonObject
        {
            ["Status"] = container.State,
            ["Running"] = container.Running,
            ["Paused"] = false,
            ["Restarting"] = false,
            ["OOMKilled"] = false,
            ["Dead"] = false,
            ["ExitCode"] = container.ExitCode,
            ["Error"] = string.Empty,
            ["StartedAt"] = Timestamp(container.StartedAtUtc),
            ["FinishedAt"] = Timestamp(container.FinishedAtUtc)
        };

        if (container.HealthStatus is { } health)
        {
            state["Health"] = new JsonObject
            {
                ["Status"] = health,
                ["FailingStreak"] = 0,
                ["Log"] = new JsonArray()
            };
        }

        return new JsonObject
        {
            ["Id"] = container.Id,
            // The daemon renders the name with a leading slash and the production mapper trims it, so a fake that
            // omitted it would leave that trim untested.
            ["Name"] = "/" + container.Name,
            ["Created"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["Image"] = "sha256:" + FakeDockerState.NewId(),
            ["State"] = state,
            ["Config"] = config,
            ["HostConfig"] = container.CreateRequest["HostConfig"]?.DeepClone() ?? new JsonObject(),
            ["NetworkSettings"] = new JsonObject
            {
                ["Ports"] = container.EffectivePorts.DeepClone()
            },
            ["Mounts"] = Mounts(container)
        };
    }

    private static JsonArray Mounts(FakeDockerContainer container)
    {
        var mounts = new JsonArray();
        foreach (var mount in container.EffectiveMounts)
        {
            var entry = new JsonObject
            {
                ["Type"] = mount.Type,
                ["Source"] = mount.Source,
                ["Destination"] = mount.Destination,
                ["Mode"] = string.Empty,
                // RW, not ReadOnly: the effective set states writability positively and the production mapper
                // negates it. Emitting a ReadOnly here instead would leave every mount reading as writable.
                ["RW"] = mount.ReadWrite,
                ["Propagation"] = string.Empty
            };

            if (mount.Name is { } name)
            {
                entry["Name"] = name;
            }

            mounts.Add(entry);
        }

        return mounts;
    }

    private static string Timestamp(DateTimeOffset? value)
    {
        return value?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) ?? NeverTimestamp;
    }
}

namespace XE_Local_AI_Engine.Testing.FakeDocker.Endpoints;

using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;

/// <summary>
///     <c>POST /containers/create?name=</c>. The request body is stored verbatim and every later inspect echoes it,
///     which is what keeps the two production creation paths distinguishable: the fields one path never sends stay
///     absent instead of arriving from a default this fake invented.
/// </summary>
internal static class ContainerCreateEndpoint
{
    /// <summary>Network modes the daemon always has and that name no user-created network.</summary>
    private static readonly string[] BuiltInNetworks = ["", "none", "host", "bridge", "default"];

    public static async Task HandleAsync(HttpContext context, FakeDockerState state)
    {
        var body = await FakeDockerEndpointMapper.ReadJsonObjectAsync(context).ConfigureAwait(false);
        if (body is null)
        {
            await FakeDockerEndpointMapper.WriteErrorAsync(context, StatusCodes.Status400BadRequest, "invalid create body")
                                          .ConfigureAwait(false);
            return;
        }

        var image = body["Image"]?.GetValue<string>() ?? string.Empty;
        if (!state.Images.TryGetValue(image, out var seededImage))
        {
            // The daemon's own wording, and a 404 rather than a 500: it is what the production client classifies
            // into the "pull it first" outcome an operator can act on.
            await FakeDockerEndpointMapper.WriteErrorAsync(context, StatusCodes.Status404NotFound, $"No such image: {image}")
                                          .ConfigureAwait(false);
            return;
        }

        var name = context.Request.Query["name"].ToString();
        if (string.IsNullOrEmpty(name))
        {
            name = body["Name"]?.GetValue<string>() ?? FakeDockerState.NewId()[..12];
        }

        if (state.Containers.Values.Any(existing => string.Equals(existing.Name, name, StringComparison.Ordinal)))
        {
            await FakeDockerEndpointMapper
                  .WriteErrorAsync(context,
                      StatusCodes.Status409Conflict,
                      $"Conflict. The container name \"/{name}\" is already in use.")
                  .ConfigureAwait(false);
            return;
        }

        var hostConfig = body["HostConfig"] as JsonObject;
        var networkMode = hostConfig?["NetworkMode"]?.GetValue<string>() ?? string.Empty;
        var attached = state.Networks.Values.FirstOrDefault(network => string.Equals(network.Name, networkMode, StringComparison.Ordinal));
        if (attached is null && !BuiltInNetworks.Contains(networkMode, StringComparer.Ordinal))
        {
            await FakeDockerEndpointMapper.WriteErrorAsync(context, StatusCodes.Status404NotFound, $"network {networkMode} not found")
                                          .ConfigureAwait(false);
            return;
        }

        var container = new FakeDockerContainer
        {
            Id = FakeDockerState.NewId(),
            Name = name,
            CreateRequest = body
        };

        AddRequestedMounts(container, hostConfig);
        AddAnonymousVolumes(container, seededImage);

        if (attached is not null)
        {
            container.AttachedNetworkIds.Add(attached.Id);
            attached.AttachedContainerIds.Add(container.Id);
        }

        state.Containers[container.Id] = container;

        await FakeDockerEndpointMapper.WriteJsonAsync(context,
                                          new JsonObject
                                          {
                                              ["Id"] = container.Id,
                                              ["Warnings"] = new JsonArray()
                                          },
                                          StatusCodes.Status201Created)
                                      .ConfigureAwait(false);
    }

    /// <summary>
    ///     The bind mounts the request asked for, copied into the effective set. They are copied rather than aliased:
    ///     the effective set and <c>HostConfig.Mounts</c> are two different answers to two different questions, and a
    ///     fake that served one from the other would pass a test asserting on either and prove nothing about both.
    /// </summary>
    private static void AddRequestedMounts(FakeDockerContainer container, JsonObject? hostConfig)
    {
        if (hostConfig?["Mounts"] is not JsonArray mounts)
        {
            return;
        }

        foreach (var mount in mounts.OfType<JsonObject>())
        {
            container.EffectiveMounts.Add(new FakeDockerMountPoint(mount["Type"]?.GetValue<string>() ?? "bind",
                mount["Source"]?.GetValue<string>() ?? string.Empty,
                mount["Target"]?.GetValue<string>() ?? string.Empty,
                ReadWrite: mount["ReadOnly"]?.GetValue<bool>() != true));
        }
    }

    /// <summary>
    ///     One anonymous volume per <c>VOLUME</c> the image declares, the way a real daemon materialises them at
    ///     create time. They appear ONLY in the effective set — never in <c>HostConfig.Mounts</c>, which the caller
    ///     never asked to contain them — and that asymmetry is the whole reason the production mapper reads the
    ///     top-level <c>Mounts[]</c>.
    /// </summary>
    private static void AddAnonymousVolumes(FakeDockerContainer container, FakeDockerImage image)
    {
        foreach (var declared in image.DeclaredVolumes)
        {
            if (container.EffectiveMounts.Any(mount => string.Equals(mount.Destination, declared, StringComparison.Ordinal)))
            {
                // An explicit mount over the declared path wins, exactly as the daemon resolves it.
                continue;
            }

            var volumeName = FakeDockerState.NewId();
            container.EffectiveMounts.Add(new FakeDockerMountPoint("volume",
                $"/var/lib/docker/volumes/{volumeName}/_data",
                declared,
                ReadWrite: true)
            {
                Name = volumeName
            });
        }
    }
}

namespace XE_Local_AI_Engine.Testing.FakeDocker.Endpoints;

using Microsoft.AspNetCore.Http;

/// <summary>
///     <c>DELETE /containers/{id}?force=&amp;v=</c>. A removed container releases the network endpoint it held, which
///     is what makes the "remove the network only after the containers" ordering observable at all.
/// </summary>
internal static class ContainerRemoveEndpoint
{
    public static Task HandleAsync(HttpContext context, FakeDockerState state, string id)
    {
        if (!state.Containers.TryRemove(id, out var container))
        {
            // 404, not silence: the production client swallows this one deliberately to make removal idempotent, and
            // a fake that answered 204 for an absent container would leave that swallow untested.
            return FakeDockerEndpointMapper.WriteErrorAsync(context, StatusCodes.Status404NotFound, $"No such container: {id}");
        }

        foreach (var network in state.Networks.Values)
        {
            network.AttachedContainerIds.Remove(container.Id);
        }

        context.Response.StatusCode = StatusCodes.Status204NoContent;
        return Task.CompletedTask;
    }
}

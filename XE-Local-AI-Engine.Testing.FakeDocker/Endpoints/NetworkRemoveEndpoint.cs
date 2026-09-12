namespace XE_Local_AI_Engine.Testing.FakeDocker.Endpoints;

using Microsoft.AspNetCore.Http;

/// <summary>
///     <c>DELETE /networks/{id-or-name}</c>. A network that still has containers attached is refused with the
///     daemon's own <c>403</c>, which is what makes teardown ordering observable; an absent one is a 404 the
///     production client swallows, which is what makes removal idempotent.
/// </summary>
internal static class NetworkRemoveEndpoint
{
    public static Task HandleAsync(HttpContext context, FakeDockerState state, string network)
    {
        var found = FakeDockerEndpointMapper.FindNetwork(state, network);
        if (found is null)
        {
            return FakeDockerEndpointMapper.WriteErrorAsync(context, StatusCodes.Status404NotFound, $"network {network} not found");
        }

        if (found.AttachedContainerIds.Count > 0)
        {
            return FakeDockerEndpointMapper.WriteErrorAsync(context,
                StatusCodes.Status403Forbidden,
                $"error while removing network: network {found.Name} id {found.Id} has active endpoints");
        }

        state.Networks.TryRemove(found.Name, out _);
        context.Response.StatusCode = StatusCodes.Status204NoContent;
        return Task.CompletedTask;
    }
}

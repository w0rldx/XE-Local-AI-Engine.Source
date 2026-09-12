namespace XE_Local_AI_Engine.Testing.FakeDocker.Endpoints;

using Microsoft.AspNetCore.Http;

/// <summary>
///     <c>GET /networks/{id-or-name}</c>. The driver and the labels are the whole point: they are what the production
///     client compares to decide whether a network that already holds the name is one it may reuse.
/// </summary>
internal static class NetworkInspectEndpoint
{
    public static Task HandleAsync(HttpContext context, FakeDockerState state, string network)
    {
        var found = FakeDockerEndpointMapper.FindNetwork(state, network);
        return found is null
            ? FakeDockerEndpointMapper.WriteErrorAsync(context, StatusCodes.Status404NotFound, $"network {network} not found")
            : FakeDockerEndpointMapper.WriteJsonAsync(context, FakeDockerEndpointMapper.DescribeNetwork(found));
    }
}

namespace XE_Local_AI_Engine.Testing.FakeDocker.Endpoints;

using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;

/// <summary>
///     <c>GET /networks?filters=</c>, filtered by the same nested <c>{"label":{"key=value":true}}</c> shape the
///     container listing uses.
/// </summary>
internal static class NetworkListEndpoint
{
    public static Task HandleAsync(HttpContext context, FakeDockerState state)
    {
        var required = FakeDockerEndpointMapper.ParseLabelFilter(context.Request.Query["filters"].ToString());

        var listed = new JsonArray();
        foreach (var network in state.Networks.Values)
        {
            if (required.All(pair => network.Labels.TryGetValue(pair.Key, out var actual)
                                     && string.Equals(actual, pair.Value, StringComparison.Ordinal)))
            {
                listed.Add(FakeDockerEndpointMapper.DescribeNetwork(network));
            }
        }

        return FakeDockerEndpointMapper.WriteJsonAsync(context, listed);
    }
}

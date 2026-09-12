namespace XE_Local_AI_Engine.Testing.FakeDocker.Endpoints;

using System.Globalization;
using Microsoft.AspNetCore.Http;

/// <summary>
///     <c>GET /containers/{id}/logs</c>. The two streams come back interleaved in Docker's 8-byte multiplexed
///     framing, because the containers this engine creates are always created without a TTY and the daemon therefore
///     always frames their logs — which is the demultiplexing the production reader exists to do.
/// </summary>
internal static class ContainerLogsEndpoint
{
    public static Task HandleAsync(HttpContext context, FakeDockerState state, string id)
    {
        if (!state.Containers.TryGetValue(id, out var container))
        {
            return FakeDockerEndpointMapper.WriteErrorAsync(context, StatusCodes.Status404NotFound, $"No such container: {id}");
        }

        var frames = (IEnumerable<FakeDockerLogFrame>)container.Logs;

        // `tail` counts frames here rather than newlines. The client sends it and the daemon honours it; what the
        // production reader is tested for is the byte ceiling it applies to what comes back, so a per-frame tail is
        // the smallest thing that lets a test ask for less than everything. `since` is recorded, not applied: the
        // fake's frames carry no clock to filter against.
        if (int.TryParse(context.Request.Query["tail"].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var tail))
        {
            frames = container.Logs.TakeLast(tail);
        }

        return FakeDockerEndpointMapper.WriteFramedAsync(context, frames);
    }
}

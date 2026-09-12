namespace XE_Local_AI_Engine.Testing.FakeDocker.Endpoints;

using Microsoft.AspNetCore.Http;

/// <summary>
///     <c>POST /containers/{id}/stop?t=</c>.
///     <para>
///         The status code carries the whole answer: Docker.DotNet maps anything other than <c>304 Not Modified</c>
///         to <c>true</c>, so 204 means "this call stopped it" and 304 means "it was already stopped". Inventing a
///         single success code here would make the idempotent half of the contract untestable.
///     </para>
/// </summary>
internal static class ContainerStopEndpoint
{
    public static Task HandleAsync(HttpContext context, FakeDockerState state, string id)
    {
        if (!state.Containers.TryGetValue(id, out var container))
        {
            return FakeDockerEndpointMapper.WriteErrorAsync(context, StatusCodes.Status404NotFound, $"No such container: {id}");
        }

        if (!container.Running)
        {
            context.Response.StatusCode = StatusCodes.Status304NotModified;
            return Task.CompletedTask;
        }

        container.Running = false;
        container.State = "exited";
        container.FinishedAtUtc = DateTimeOffset.UtcNow;

        context.Response.StatusCode = StatusCodes.Status204NoContent;
        return Task.CompletedTask;
    }
}

namespace XE_Local_AI_Engine.Client.ExceptionHandling;

using Microsoft.AspNetCore.Diagnostics;
using XE_Local_AI_Engine.Client.Services.Containers;

/// <summary>
///     Maps "there is no usable container runtime" to a 503, for the routes that reach
///     <c>IContainerRuntimeResolver.CreateRuntimeAsync</c> inside the request rather than inside a background operation.
/// </summary>
/// <remarks>
///     503 rather than 409: the request is well-formed and will succeed once the daemon is back. The detail is the
///     resolution's own operator message, already redacted to <c>DockerDaemonEndpoint.Display</c>, and never a daemon
///     exception message, which can carry the environment it failed on. See ADR 0009 and
///     docs/wiki/09-api-and-hubs.md ("Status choices worth their reasons").
/// </remarks>
public sealed class ContainerRuntimeUnavailableExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is not ContainerRuntimeUnavailableException unavailable)
        {
            return false;
        }

        await FastEndpointsProblemWriter
            .WriteAsync(httpContext, unavailable.Message, StatusCodes.Status503ServiceUnavailable, cancellationToken);
        return true;
    }
}

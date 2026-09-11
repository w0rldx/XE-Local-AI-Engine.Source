namespace XE_Local_AI_Engine.Client.ExceptionHandling;

using Microsoft.AspNetCore.Diagnostics;
using XE_Local_AI_Engine.Client.Services.Containers;

/// <summary>
///     Maps "there is no usable container runtime" to a 503, for the routes that reach
///     <c>IContainerRuntimeResolver.CreateRuntimeAsync</c> inside the request rather than inside a background
///     operation. Without it the read of a log tail on a node whose Docker daemon is down is a 500, which tells the
///     operator the engine broke rather than that the machine has an obstacle to clear.
///     <para>
///         503 rather than a 409: this is not a state transition the operator can decline, and it is not a conflict
///         with anything the node holds — the request is well-formed and will succeed once the daemon is back, which
///         is what "the service is temporarily unable" means. ADR 0009's Blocked-family carve-out does not apply:
///         those exist because the refusal is a value the service RETURNS, whereas this one can only be thrown.
///     </para>
///     <para>
///         The detail is the resolution's own operator message — the same prose the runtime card renders, already
///         redacted to <c>DockerDaemonEndpoint.Display</c> — and never a daemon exception message, which can carry the
///         environment it failed on. The lifecycle verbs are unaffected: they admit with a 202 and settle the same
///         unavailability into the row's <c>RuntimeUnavailable</c> failure category.
///     </para>
/// </summary>
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
              .WriteAsync(httpContext, unavailable.Message, StatusCodes.Status503ServiceUnavailable, cancellationToken)
              .ConfigureAwait(false);
        return true;
    }
}

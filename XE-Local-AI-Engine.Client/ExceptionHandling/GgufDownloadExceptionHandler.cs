namespace XE_Local_AI_Engine.Client.ExceptionHandling;

using Microsoft.AspNetCore.Diagnostics;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

/// <summary>
///     Maps the synchronous failures of starting a GGUF download through the wire-contract helper its three consuming
///     endpoints already shared, following the <see cref="TrainingExceptionHandler" /> /
///     <see cref="BenchmarkExceptionHandler" /> precedent. The narrowed family
///     (<see cref="GgufDownloadEndpointSupport.IsHandled" />) and the ProblemDetails bodies it produces are unchanged;
///     anything outside that family still reaches the default 500.
/// </summary>
public sealed class GgufDownloadExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        if (!GgufDownloadEndpointSupport.IsHandled(exception))
        {
            return false;
        }

        await GgufDownloadEndpointSupport.Error(exception).ExecuteAsync(httpContext);
        return true;
    }
}

namespace XE_Local_AI_Engine.Client.ExceptionHandling;

using Microsoft.AspNetCore.Diagnostics;
using XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

/// <summary>
///     Maps route-invariant Benchmark exceptions through the existing RFC 7807 helper. Batch-cell failures,
///     <see cref="KeyNotFoundException" />, and unsupported runtime context remain endpoint-owned.
/// </summary>
public sealed class BenchmarkExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        if (!BenchmarkEndpointSupport.IsHandled(exception))
        {
            return false;
        }

        await BenchmarkEndpointSupport.Error(exception).ExecuteAsync(httpContext).ConfigureAwait(false);
        return true;
    }
}

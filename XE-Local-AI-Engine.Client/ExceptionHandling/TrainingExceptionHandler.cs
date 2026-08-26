namespace XE_Local_AI_Engine.Client.ExceptionHandling;

using Microsoft.AspNetCore.Diagnostics;
using XE_Local_AI_Engine.Client.Endpoints.Training.V1;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Maps the Training store exception family through its existing wire-contract helper. Contextual exceptions such
///     as runtime/install failures, operator-facing rejections, and <see cref="KeyNotFoundException" /> deliberately
///     fall through to later handlers or stay at their endpoint.
/// </summary>
public sealed class TrainingExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is not TrainingStoreException)
        {
            return false;
        }

        await TrainingEndpointSupport.Error(exception).ExecuteAsync(httpContext).ConfigureAwait(false);
        return true;
    }
}

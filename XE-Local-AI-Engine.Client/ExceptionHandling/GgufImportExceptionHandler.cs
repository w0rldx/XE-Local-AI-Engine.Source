namespace XE_Local_AI_Engine.Client.ExceptionHandling;

using Microsoft.AspNetCore.Diagnostics;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;
using XE_Local_AI_Engine.Client.Services.ModelFit;

/// <summary>
///     Maps the GGUF import pipeline's synchronous failures through the wire-contract helper the endpoints already
///     shared, following the <see cref="TrainingExceptionHandler" /> / <see cref="BenchmarkExceptionHandler" />
///     precedent: the mapping was already in one place, only the catch was duplicated. The status split
///     (409 / 404 / 507 / 400) and the <c>GgufImportErrorResponse</c> body are
///     <see cref="GgufImportEndpointSupport.Error" />'s, unchanged.
/// </summary>
public sealed class GgufImportExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is not GgufImportApplicationException importFailure)
        {
            return false;
        }

        await GgufImportEndpointSupport.Error(importFailure).ExecuteAsync(httpContext);
        return true;
    }
}

namespace XE_Local_AI_Engine.Client.ExceptionHandling;

using Microsoft.AspNetCore.Diagnostics;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Maps every <see cref="ResourceNotFoundException" /> to the surface's bodyless 404 response. Registered before
///     <c>DefaultExceptionHandler</c>, which would otherwise turn a missing resource into a 500.
/// </summary>
/// <remarks>
///     It answers the TYPED family only: a bare <see cref="KeyNotFoundException" /> from anywhere else in the pipeline
///     still reaches the default 500, so an unrelated dictionary miss cannot present itself as "not found".
/// </remarks>
public sealed class ResourceNotFoundExceptionHandler : IExceptionHandler
{
    public ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is not ResourceNotFoundException)
        {
            return ValueTask.FromResult(false);
        }

        httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
        httpContext.Response.ContentType = null;
        httpContext.Response.ContentLength = null;
        return ValueTask.FromResult(true);
    }
}

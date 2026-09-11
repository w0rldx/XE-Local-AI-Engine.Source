namespace XE_Local_AI_Engine.Client.ExceptionHandling;

using Microsoft.AspNetCore.Diagnostics;
using XE_Local_AI_Engine.Client.Services.ExternalApps;

/// <summary>
///     Maps a missing external-app instance, service or catalog application to the surface's bodyless 404 response.
///     Registered before <c>DefaultExceptionHandler</c>, which would otherwise turn a missing instance into a 500.
/// </summary>
public sealed class ExternalAppNotFoundExceptionHandler : IExceptionHandler
{
    public ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is not ExternalAppNotFoundException)
        {
            return ValueTask.FromResult(false);
        }

        httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
        httpContext.Response.ContentType = null;
        httpContext.Response.ContentLength = null;
        return ValueTask.FromResult(true);
    }
}

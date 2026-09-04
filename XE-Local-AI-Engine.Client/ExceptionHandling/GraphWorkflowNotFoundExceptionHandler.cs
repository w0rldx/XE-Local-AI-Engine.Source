namespace XE_Local_AI_Engine.Client.ExceptionHandling;

using Microsoft.AspNetCore.Diagnostics;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Maps missing graph-workflow resources to the surface's bodyless 404 response. Registered before
///     <c>DefaultExceptionHandler</c>, which would otherwise turn a missing definition into a 500.
///     <para>
///         Its own handler rather than an arm on the development-workflow one: the two families share no exception
///         type, and widening that handler would make one surface's 404 depend on the other's exception set.
///     </para>
/// </summary>
public sealed class GraphWorkflowNotFoundExceptionHandler : IExceptionHandler
{
    public ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is not GraphWorkflowNotFoundException)
        {
            return ValueTask.FromResult(false);
        }

        httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
        httpContext.Response.ContentType = null;
        httpContext.Response.ContentLength = null;
        return ValueTask.FromResult(true);
    }
}

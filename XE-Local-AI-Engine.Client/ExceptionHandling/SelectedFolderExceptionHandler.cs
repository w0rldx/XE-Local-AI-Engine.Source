namespace XE_Local_AI_Engine.Client.ExceptionHandling;

using Microsoft.AspNetCore.Diagnostics;
using XE_Local_AI_Engine.Client.Services.Workspace;

/// <summary>
///     The single selected-folder exception → HTTP mapping, for every endpoint that registers or resolves a selected
///     folder. The exception TYPE carries the status — unknown id → 404, alias/state conflict → 409, any other
///     rejection the aggregate type reports → 400 — so the same rejection cannot answer differently depending on which
///     endpoint produced it. It replaces <c>SelectedFolderEndpointSupport</c>, which held this mapping but needed an
///     endpoint's <c>Send</c> to write it, so eight endpoints carried the identical catch to call it.
///     <para>
///         The arm order is load-bearing: both specific types derive from the aggregate, so they must be matched before
///         it. That inheritance is also why the family stays out of <see cref="DomainValidationExceptionHandler" /> — a
///         global 400 there would flatten all three statuses into one.
///     </para>
/// </summary>
public sealed class SelectedFolderExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        switch (exception)
        {
            case SelectedFolderNotFoundException:
                httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
                httpContext.Response.ContentType = null;
                httpContext.Response.ContentLength = null;
                return true;

            case SelectedFolderConflictException:
                await FastEndpointsProblemWriter.WriteAsync(httpContext, exception.Message, StatusCodes.Status409Conflict, cancellationToken);
                return true;

            case SelectedFolderValidationException:
                await FastEndpointsProblemWriter.WriteAsync(httpContext, exception.Message, StatusCodes.Status400BadRequest, cancellationToken);
                return true;

            default:
                return false;
        }
    }
}

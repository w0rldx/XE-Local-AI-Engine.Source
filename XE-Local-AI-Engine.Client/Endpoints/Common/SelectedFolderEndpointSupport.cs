namespace XE_Local_AI_Engine.Client.Endpoints.Common;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Services.Workspace;

/// <summary>
///     Single selected-folder exception → HTTP mapping, shared by every endpoint that registers or resolves a selected
///     folder. The exception TYPE carries the status — unknown id → 404, alias/state conflict → 409, any other
///     rejection the aggregate type reports → 400 — so the same rejection cannot answer differently depending on which
///     endpoint produced it.
///     <para>
///         The family is deliberately absent from <c>DomainValidationExceptionHandler</c>:
///         <see cref="SelectedFolderValidationException" /> is the base of both the 404 and the 409 type, so a global
///         400 mapping would flatten all three into one status. The arm order in <see cref="SendAsync" /> is therefore
///         load-bearing — the two derived types must be matched before their base.
///     </para>
/// </summary>
internal static class SelectedFolderEndpointSupport
{
    /// <summary>Exception-filter predicate: true for the whole family, since both specific types derive from the aggregate.</summary>
    public static bool IsHandled(Exception exception) =>
        exception is SelectedFolderValidationException;

    /// <summary>
    ///     Writes the response for a <see cref="IsHandled" /> exception. Anything else is rethrown so a genuine fault
    ///     still reaches the global handler rather than being flattened into a 4xx. The endpoint passes both itself
    ///     (for <c>AddError</c>) and its <c>Send</c> sender, which is protected and therefore not reachable from here.
    /// </summary>
    public static Task SendAsync<TRequest, TResponse>(IValidationErrors errors,
        ResponseSender<TRequest, TResponse> send,
        Exception exception,
        CancellationToken ct)
        where TRequest : notnull
    {
        ArgumentNullException.ThrowIfNull(errors);
        ArgumentNullException.ThrowIfNull(exception);

        switch (exception)
        {
            case SelectedFolderNotFoundException:
                return send.NotFoundAsync(ct);

            case SelectedFolderConflictException:
                errors.AddError(exception.Message);
                return send.ErrorsAsync(statusCode: StatusCodes.Status409Conflict, cancellation: ct);

            case SelectedFolderValidationException:
                errors.AddError(exception.Message);
                return send.ErrorsAsync(cancellation: ct);

            default:
                throw exception;
        }
    }
}

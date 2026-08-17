namespace XE_Local_AI_Engine.Client.Endpoints.Common;

using FastEndpoints;

/// <summary>
///     One construction of the 409 <c>application/problem+json</c> body an endpoint writes itself, for the outcome-enum
///     case: a service reports a conflict as a return value rather than an exception, so
///     <c>ConflictExceptionHandler</c> never sees it and its <c>ConflictProblemDetails</c> envelope does not apply.
///     <para>
///         This is the plain ASP.NET <c>ProblemDetails</c> shape — declare it with ASP.NET's own
///         <c>ProducesProblem(409)</c>, never
///         <see cref="ProblemDetailsProducesExtensions.ProducesConflictProblemDetails" />; see that type for the three
///         distinct error bodies this API writes.
///     </para>
/// </summary>
internal static class ConflictProblemSenderExtensions
{
    /// <param name="sender">The endpoint's response sender.</param>
    /// <param name="detail">Operator-safe explanation, written as the ProblemDetails <c>detail</c>.</param>
    /// <param name="extensions">Optional machine-readable members (e.g. an <c>outcome</c> discriminator).</param>
    public static Task ConflictProblemAsync<TRequest, TResponse>(this ResponseSender<TRequest, TResponse> sender,
        string detail,
        IDictionary<string, object?>? extensions = null)
        where TRequest : notnull
    {
        return sender.ResultAsync(Results.Problem(statusCode: StatusCodes.Status409Conflict, detail: detail, extensions: extensions));
    }
}

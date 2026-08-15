namespace XE_Local_AI_Engine.Client.Endpoints.Common;

using XE_Local_AI_Engine.Client.Common.ProblemDetailModels;

/// <summary>
///     OpenAPI response metadata for the error bodies this API actually writes. Three shapes exist, and each has its
///     own declaration — declaring the wrong one is a contract lie the generated client encodes:
///     <list type="bullet">
///         <item>
///             FastEndpoints' own <c>AddError + Send.ErrorsAsync</c> body (and the global
///             <c>DomainValidationExceptionHandler</c>, which mimics it) → FastEndpoints' <c>ProducesProblemDetails(status)</c>.
///             Its schema is <c>additionalProperties: false</c> with an <c>errors[]</c> member.
///         </item>
///         <item>
///             A body written by <c>ConflictExceptionHandler</c> → <see cref="ProducesConflictProblemDetails" /> here:
///             the <see cref="ConflictProblemDetails" /> envelope, whose <c>conflictType</c> discriminator and cap
///             members the FastEndpoints schema does not know.
///         </item>
///         <item>
///             A body written by <c>Results.Problem(...)</c> (ASP.NET <c>ProblemDetails</c>, extension members such as
///             <c>code</c>/<c>outcome</c>/<c>traceId</c> allowed) → ASP.NET's own <c>ProducesProblem(status)</c>, whose
///             schema permits additional properties.
///         </item>
///     </list>
/// </summary>
public static class ProblemDetailsProducesExtensions
{
    /// <summary>Declares the 409 <see cref="ConflictProblemDetails" /> envelope <c>ConflictExceptionHandler</c> writes.</summary>
    public static RouteHandlerBuilder ProducesConflictProblemDetails(this RouteHandlerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.Produces<ConflictProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json");
    }
}

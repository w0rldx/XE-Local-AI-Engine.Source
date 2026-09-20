namespace XE_Local_AI_Engine.Client.Endpoints.Common;

using XE_Local_AI_Engine.Client.Common.ProblemDetailModels;

/// <summary>
///     OpenAPI response metadata for the error bodies this API actually writes. Three shapes exist, each with its own
///     declaration; declaring the wrong one is a contract lie the generated client encodes.
/// </summary>
/// <remarks>
///     Which body gets which declaration: docs/wiki/09-api-and-hubs.md
///     ("The three error bodies and their declarations").
/// </remarks>
public static class ProblemDetailsProducesExtensions
{
    /// <summary>Declares the 409 <see cref="ConflictProblemDetails" /> envelope <c>ConflictExceptionHandler</c> writes.</summary>
    public static RouteHandlerBuilder ProducesConflictProblemDetails(this RouteHandlerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.Produces<ConflictProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json");
    }
}

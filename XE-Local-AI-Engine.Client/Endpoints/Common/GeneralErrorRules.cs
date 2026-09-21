namespace XE_Local_AI_Engine.Client.Endpoints.Common;

using FastEndpoints;
using FluentValidation;
using FluentValidation.Results;

/// <summary>
///     Carries a handler's pure request-shape check into its validator unchanged, reporting under the general-errors
///     field the handler's own <c>AddError(message)</c> reported under.
/// </summary>
/// <remarks>
///     The checks this replaces are <c>if</c> chains over the whole request that returned on the first violation, so
///     the rule reads the whole request and yields at most one message — the cascade never comes into it. The failure
///     names the general-errors field rather than the property the rule hangs on, because that is the entry the client
///     reads. Nothing here reaches the OpenAPI schema: the processor reflects only the declarative rule kinds.
/// </remarks>
internal static class GeneralErrorRules
{
    // FastEndpoints' ErrorOptions.GeneralErrorsField default — the property AddError(message) attached its failure to.
    private const string GeneralErrorsField = "GeneralErrors";

    /// <summary>
    ///     Adds one rule reporting <paramref name="firstViolation" />'s message, or nothing when it returns null.
    /// </summary>
    public static void AddFirstViolationRule<TRequest>(this Validator<TRequest> validator,
        Func<TRequest, string?> firstViolation)
        where TRequest : class
    {
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(firstViolation);

        validator.RuleFor(static request => request)
                 .Custom((request, context) =>
                 {
                     if (firstViolation(request) is { } message)
                     {
                         context.AddFailure(new ValidationFailure(GeneralErrorsField, message));
                     }
                 });
    }
}

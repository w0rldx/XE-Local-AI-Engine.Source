namespace XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1.Validators;

using System.Linq.Expressions;
using FastEndpoints;
using FluentValidation;
using FluentValidation.Results;
using XE_Local_AI_Engine.Client.Services.Validation;

/// <summary>
///     The shared model-name grammar rule, in the two forms the local-model surface needs.
/// </summary>
/// <remarks>
///     The rule must judge the exact string its handler goes on to use, so where the name was bound decides whether
///     it is decoded first — a route segment arrives carrying a literal <c>%2F</c>, a JSON body does not and its
///     handler never decodes. Both forms report unkeyed, under the general-errors field <c>AddError(message)</c>
///     used, because that is the entry the client reads.
/// </remarks>
internal static class ModelNameRules
{
    // FastEndpoints' ErrorOptions.GeneralErrorsField default — the property AddError(message) attached its failure to.
    // Mirrors FastEndpointsProblemWriter, which pins the same literal for the same reason.
    private const string GeneralErrorsField = "GeneralErrors";

    /// <summary>
    ///     The grammar over a name bound from a <c>{modelName}</c> route segment, applied AFTER decoding it.
    /// </summary>
    /// <remarks>
    ///     Kestrel leaves <c>%2F</c> and <c>%5C</c> encoded by design, so a Hugging Face reference reaches the server
    ///     still escaped and its <c>%</c> would fail the grammar. The handler decodes and uses the decoded name, so
    ///     the rule judges that same decoded name. See <see cref="ModelRouteName" />.
    /// </remarks>
    public static void AddRouteBoundModelNameRule<TRequest>(this Validator<TRequest> validator,
        Expression<Func<TRequest, string?>> modelName)
        where TRequest : class =>
        validator.AddModelNameRule(modelName, ModelRouteName.Decode);

    /// <summary>
    ///     The grammar over a name bound from the request body, applied to the RAW value.
    /// </summary>
    /// <remarks>
    ///     Decoding here would accept a name no handler ever decodes: <c>ext%3Aconn%2Ffoo</c> fails the grammar as
    ///     written but decodes to a well-formed external id, and the select handler would then store the escaped
    ///     string and skip the external-registration guard, which does not recognise it as external.
    /// </remarks>
    public static void AddBodyBoundModelNameRule<TRequest>(this Validator<TRequest> validator,
        Expression<Func<TRequest, string?>> modelName)
        where TRequest : class =>
        validator.AddModelNameRule(modelName, static value => value);

    private static void AddModelNameRule<TRequest>(this Validator<TRequest> validator,
        Expression<Func<TRequest, string?>> modelName,
        Func<string?, string?> asTheHandlerReadsIt)
        where TRequest : class
    {
        ArgumentNullException.ThrowIfNull(validator);

        validator.RuleFor(modelName)
                 .Custom((value, context) =>
                 {
                     var error = validator.Resolve<ModelNameValidator>().GetValidationError(asTheHandlerReadsIt(value));
                     if (error is not null)
                     {
                         context.AddFailure(new ValidationFailure(GeneralErrorsField, error));
                     }
                 });
    }
}

namespace XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1.Validators;

using FastEndpoints;
using FluentValidation;
using FluentValidation.Results;
using XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1.Mappers;

/// <summary>
///     The model-name grammar and the classification-override kind, in the order the handler checked them.
/// </summary>
/// <remarks>
///     The handler returned on the first failure, so the cascade stops: an unsafe name beside an undefined kind still
///     reports only the name. Both failures stay unkeyed, matching the <c>AddError(message)</c> they replace.
/// </remarks>
public sealed class SetModelKindRequestValidator : Validator<SetModelKindRequest>
{
    // FastEndpoints' ErrorOptions.GeneralErrorsField default — see ModelNameRules for why this is a literal.
    private const string GeneralErrorsField = "GeneralErrors";

    public SetModelKindRequestValidator()
    {
        ClassLevelCascadeMode = CascadeMode.Stop;

        this.AddRouteBoundModelNameRule(static request => request.ModelName);

        RuleFor(static request => request.Kind)
            .Custom(static (kind, context) =>
            {
                if (!LocalModelsMapper.TryParseKind(kind, out _))
                {
                    context.AddFailure(new ValidationFailure(GeneralErrorsField, "Invalid model kind"));
                }
            });
    }
}

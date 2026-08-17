namespace XE_Local_AI_Engine.Client.Endpoints.ApiFoundation.V1.Validators;

using FastEndpoints;
using FluentValidation;

public sealed class ValidationProblemProbeRequestValidator : Validator<ValidationProblemProbeRequest>
{
    public ValidationProblemProbeRequestValidator()
    {
        RuleFor(static request => request.Name)
            .NotEmpty()
            .MaximumLength(64);
    }
}

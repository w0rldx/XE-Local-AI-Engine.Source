namespace XE_Local_AI_Engine.Client.Endpoints.ModelLab.V1.Validators;

using FastEndpoints;
using FluentValidation;

/// <summary>Both names present and bounded; whether the file is served is the index's answer, not the grammar's.</summary>
public sealed class GetModelLabPyTorchSourceRequestValidator : Validator<GetModelLabPyTorchSourceRequest>
{
    public GetModelLabPyTorchSourceRequestValidator()
    {
        RuleFor(static request => request.File)
            .NotEmpty()
            .MaximumLength(512);

        RuleFor(static request => request.Index)
            .NotEmpty()
            .MaximumLength(200);
    }
}

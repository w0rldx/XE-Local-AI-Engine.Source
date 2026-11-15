namespace XE_Local_AI_Engine.Client.Endpoints.ModelLab.V1.Validators;

using FastEndpoints;
using FluentValidation;
using XE_Local_AI_Engine.Client.Services.ModelLab;

public sealed class SetModelLabExperimentNotesRequestValidator : Validator<SetModelLabExperimentNotesRequest>
{
    public SetModelLabExperimentNotesRequestValidator()
    {
        RuleFor(static request => request.Notes)
            .MaximumLength(ModelLabExperimentService.MaxNotesCharacters)
            .WithMessage($"A note can be at most {ModelLabExperimentService.MaxNotesCharacters} characters.");
    }
}

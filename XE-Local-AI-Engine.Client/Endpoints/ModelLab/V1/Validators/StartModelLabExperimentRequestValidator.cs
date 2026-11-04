namespace XE_Local_AI_Engine.Client.Endpoints.ModelLab.V1.Validators;

using FastEndpoints;
using FluentValidation;
using XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1.Validators;
using XE_Local_AI_Engine.Client.Services.ModelLab;

public sealed class StartModelLabExperimentRequestValidator : Validator<StartModelLabExperimentRequest>
{
    public StartModelLabExperimentRequestValidator()
    {
        RuleFor(static request => request.BaseArtifactId)
            .NotEmpty()
            .WithMessage("Choose a downloaded checkpoint.");

        RuleFor(static request => request.Prompt)
            .NotEmpty()
            .Must(static prompt => !string.IsNullOrWhiteSpace(prompt))
            .WithMessage("Enter a prompt to trace.")
            .MaximumLength(ModelLabTraceSpec.MaxPromptCharacters);

        RuleFor(static request => request.InstalledModelName)
            .NotEmpty()
            .WithMessage("Choose the installed GGUF that will apply the vector.");

        this.AddBodyBoundModelNameRule(static request => request.InstalledModelName);
    }
}

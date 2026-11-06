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

        RuleFor(static request => request.Steering!)
            .SetValidator(new ModelLabSteeringRequestValidator())
            .When(static request => request.Steering is not null);

        // The range depends on the checkpoint, so the start decides it.
        RuleFor(static request => request.Layers!)
            .Must(static layers => layers.Count is >= 1 and <= ModelLabTraceSpec.MaxTraceLayers
                                   && layers.All(static layer => layer >= 0)
                                   && layers.Distinct().Count() == layers.Count)
            .WithMessage($"Choose 1 to {ModelLabTraceSpec.MaxTraceLayers} distinct trace layers, none below 0.")
            .When(static request => request.Layers is not null);

        RuleFor(static request => request.Layers)
            .Null()
            .WithMessage("A steering job has its own layer. Leave out the trace layers.")
            .When(static request => request.Steering is not null);
    }
}

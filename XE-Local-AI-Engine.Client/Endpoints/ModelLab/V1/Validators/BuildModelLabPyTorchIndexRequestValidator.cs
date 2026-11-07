namespace XE_Local_AI_Engine.Client.Endpoints.ModelLab.V1.Validators;

using FastEndpoints;
using FluentValidation;
using XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1.Validators;
using XE_Local_AI_Engine.Client.Endpoints.Training.BaseArtifacts.V1.Validators;

/// <summary>The model-name grammar, and exactly one well-formed config source.</summary>
public sealed class BuildModelLabPyTorchIndexRequestValidator : Validator<BuildModelLabPyTorchIndexRequest>
{
    public BuildModelLabPyTorchIndexRequestValidator()
    {
        this.AddRouteBoundModelNameRule(static request => request.ModelName);

        RuleFor(static request => request.Source)
            .Must(static source => source is "checkpoint" or "huggingface")
            .WithMessage("The source must be 'checkpoint' or 'huggingface'.");

        RuleFor(static request => request.BaseArtifactId)
            .Must(static id => id is { } value && value != Guid.Empty)
            .When(static request => request.Source == "checkpoint")
            .WithMessage("Choose the downloaded checkpoint whose config.json to read.");

        RuleFor(static request => request.RepoId)
            .Must(CreateBaseArtifactRequestValidator.BeAHuggingFaceRepoId)
            .When(static request => request.Source == "huggingface")
            .WithMessage("The repository id must be in owner/name form, for example Qwen/Qwen3.5-0.8B.");

        RuleFor(static request => request.Revision!)
            .MaximumLength(100)
            .When(static request => !string.IsNullOrWhiteSpace(request.Revision));
    }
}

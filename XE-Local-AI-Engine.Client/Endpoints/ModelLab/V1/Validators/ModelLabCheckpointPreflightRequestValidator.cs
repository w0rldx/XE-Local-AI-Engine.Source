namespace XE_Local_AI_Engine.Client.Endpoints.ModelLab.V1.Validators;

using FastEndpoints;
using FluentValidation;
using XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1.Validators;
using XE_Local_AI_Engine.Client.Endpoints.Training.BaseArtifacts.V1.Validators;

public sealed class ModelLabCheckpointPreflightRequestValidator : Validator<ModelLabCheckpointPreflightRequest>
{
    public ModelLabCheckpointPreflightRequestValidator()
    {
        RuleFor(static request => request.RepoId)
            .Must(CreateBaseArtifactRequestValidator.BeAHuggingFaceRepoId)
            .WithMessage("The repository id must be in owner/name form, for example Qwen/Qwen3.5-0.8B.")
            .When(static request => request.BaseArtifactId is null);

        RuleFor(static request => request.Revision!)
            .MaximumLength(100)
            .When(static request => !string.IsNullOrWhiteSpace(request.Revision));

        RuleFor(static request => request.InstalledModelName)
            .NotEmpty()
            .WithMessage("Choose the installed GGUF that will apply the vector.");

        this.AddBodyBoundModelNameRule(static request => request.InstalledModelName);
    }
}

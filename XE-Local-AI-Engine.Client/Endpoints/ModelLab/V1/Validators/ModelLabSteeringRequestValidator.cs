namespace XE_Local_AI_Engine.Client.Endpoints.ModelLab.V1.Validators;

using FluentValidation;
using XE_Local_AI_Engine.Client.Services.ModelLab;

/// <summary>Bounds what a request can make the job do. The layer's range depends on the model, so the start decides it.</summary>
internal sealed class ModelLabSteeringRequestValidator : AbstractValidator<ModelLabSteeringRequest>
{
    public ModelLabSteeringRequestValidator()
    {
        RuleFor(static request => request.PositivePrompts)
            .Must(static prompts => UsablePrompts(prompts, 1, ModelLabTraceSpec.MaxContrastPrompts, ModelLabTraceSpec.MaxContrastPromptCharacters))
            .WithMessage($"Enter 1 to {ModelLabTraceSpec.MaxContrastPrompts} positive prompts of at most {ModelLabTraceSpec.MaxContrastPromptCharacters} characters.");

        RuleFor(static request => request.NegativePrompts)
            .Must(static prompts => UsablePrompts(prompts, 1, ModelLabTraceSpec.MaxContrastPrompts, ModelLabTraceSpec.MaxContrastPromptCharacters))
            .WithMessage($"Enter 1 to {ModelLabTraceSpec.MaxContrastPrompts} negative prompts of at most {ModelLabTraceSpec.MaxContrastPromptCharacters} characters.");

        RuleFor(static request => request.AdditionalTestPrompts)
            .Must(static prompts => UsablePrompts(prompts, 0, ModelLabTraceSpec.MaxAdditionalTestPrompts, ModelLabTraceSpec.MaxPromptCharacters))
            .WithMessage($"Enter at most {ModelLabTraceSpec.MaxAdditionalTestPrompts} more test prompts of at most {ModelLabTraceSpec.MaxPromptCharacters} characters.");

        RuleFor(static request => request.Scale)
            .Must(static scale => Math.Abs(scale) is >= ModelLabTraceSpec.MinSteeringScale and <= ModelLabTraceSpec.MaxSteeringScale)
            .WithMessage($"The scale must be between {ModelLabTraceSpec.MinSteeringScale} and {ModelLabTraceSpec.MaxSteeringScale}, or the same below zero.");
    }

    private static bool UsablePrompts(IReadOnlyList<string>? prompts, int minimum, int maximum, int characters) =>
        prompts is not null
        && prompts.Count >= minimum
        && prompts.Count <= maximum
        && prompts.All(prompt => !string.IsNullOrWhiteSpace(prompt) && prompt.Length <= characters);
}

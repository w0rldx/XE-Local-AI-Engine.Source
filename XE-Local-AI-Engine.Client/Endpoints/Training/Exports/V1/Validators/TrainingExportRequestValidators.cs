namespace XE_Local_AI_Engine.Client.Endpoints.Training.Exports.V1.Validators;

using FastEndpoints;
using FluentValidation;
using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Boundary validation for the export surface. The request DTOs cannot use <c>required</c> — they mix route and
///     body binding — so the "you must actually say what to export" rule lives here, where it produces a field
///     message rather than a serializer error.
/// </summary>
public sealed class StartTrainingExportRequestValidator : Validator<StartTrainingExportRequest>
{
    public StartTrainingExportRequestValidator()
    {
        RuleFor(static request => request.RunId).NotEmpty().WithMessage("A run id is required.");

        RuleFor(static request => request.Kind)
            .NotNull()
            .WithMessage("An export kind is required.")
            // Without this, an omitted kind would bind to the enum's zero value and silently export something other
            // than what the operator asked for.
            .Must(static kind => kind is TrainingArtifactKind.MergedGguf or TrainingArtifactKind.AdapterGguf)
            .WithMessage("The export kind must be MergedGguf or AdapterGguf.");
    }
}

public sealed class PromoteTrainingArtifactRequestValidator : Validator<PromoteTrainingArtifactRequest>
{
    public PromoteTrainingArtifactRequestValidator()
    {
        RuleFor(static request => request.ArtifactId).NotEmpty().WithMessage("An artifact id is required.");
        RuleFor(static request => request.ModelName).NotEmpty().WithMessage("A model name is required.");
    }
}

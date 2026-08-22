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

public sealed class DecideArtifactQualityRequestValidator : Validator<DecideArtifactQualityRequest>
{
    public DecideArtifactQualityRequestValidator()
    {
        RuleFor(static request => request.ArtifactId).NotEmpty().WithMessage("An artifact id is required.");
        RuleFor(static request => request.ComparisonId).NotEmpty().WithMessage("A comparison id is required.");
        RuleFor(static request => request.ExpectedVersion).NotNull().WithMessage("An expected version is required.")
                                                          .GreaterThanOrEqualTo(0).WithMessage("An expected version is required.");
    }
}

public sealed class OverrideArtifactQualityRequestValidator : Validator<OverrideArtifactQualityRequest>
{
    public OverrideArtifactQualityRequestValidator()
    {
        RuleFor(static request => request.ArtifactId).NotEmpty().WithMessage("An artifact id is required.");
        RuleFor(static request => request.ExpectedVersion).NotNull().WithMessage("An expected version is required.")
                                                          .GreaterThanOrEqualTo(0).WithMessage("An expected version is required.");
        RuleFor(static request => request.Reason).NotEmpty().WithMessage("An override reason is required.")
                                                 .MaximumLength(1024).WithMessage("An override reason cannot exceed 1024 characters.");
    }
}

public sealed class BeginArtifactQualityRevalidationRequestValidator : Validator<BeginArtifactQualityRevalidationRequest>
{
    public BeginArtifactQualityRevalidationRequestValidator()
    {
        RuleFor(static request => request.ArtifactId).NotEmpty().WithMessage("An artifact id is required.");
        RuleFor(static request => request.ExpectedVersion).NotNull().WithMessage("An expected version is required.")
                                                          .GreaterThanOrEqualTo(0).WithMessage("An expected version is required.");
    }
}

public sealed class DiscardArtifactQualityRequestValidator : Validator<DiscardArtifactQualityRequest>
{
    public DiscardArtifactQualityRequestValidator()
    {
        RuleFor(static request => request.ArtifactId).NotEmpty().WithMessage("An artifact id is required.");
        RuleFor(static request => request.ExpectedVersion).NotNull().WithMessage("An expected version is required.")
                                                          .GreaterThanOrEqualTo(0).WithMessage("An expected version is required.");
        RuleFor(static request => request.Reason).NotEmpty().WithMessage("A discard reason is required.")
                                                 .MaximumLength(1024).WithMessage("A discard reason cannot exceed 1024 characters.");
    }
}

namespace XE_Local_AI_Engine.Client.Endpoints.Training.Evaluations.V1.Validators;

using FastEndpoints;
using FluentValidation;
using XE_Local_AI_Engine.Client.Services.Training.Evaluation;

/// <summary>
///     Shape validation for <see cref="CreateEvaluationRequest" />: a run id must be present and the target must name
///     a side of the comparison.
/// </summary>
/// <remarks>
///     Whether the run, model or artifact actually exists is the evaluation service's call: that needs a database, and
///     a probe does not belong in a validator.
/// </remarks>
public sealed class CreateEvaluationRequestValidator : Validator<CreateEvaluationRequest>
{
    public CreateEvaluationRequestValidator()
    {
        RuleFor(static request => request.TrainingRunId).NotEmpty().WithMessage("A training run id is required.");
        RuleFor(static request => request.Target)
            .Must(static target => target is EvaluationTarget.Base or EvaluationTarget.Tuned)
            .WithMessage("An evaluation target is required.");
    }
}

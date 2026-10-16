namespace XE_Local_AI_Engine.Client.Endpoints.Training.Evaluations.V1.Validators;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;

public sealed class DeleteEvaluationRequestValidator : Validator<DeleteEvaluationRequest>
{
    public DeleteEvaluationRequestValidator() =>
        ExpectedVersionRule.Require(this, static request => request.ExpectedVersion, "expectedVersion", "evaluation");
}

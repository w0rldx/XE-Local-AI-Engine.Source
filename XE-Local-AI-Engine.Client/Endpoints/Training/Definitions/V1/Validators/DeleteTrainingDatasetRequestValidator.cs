namespace XE_Local_AI_Engine.Client.Endpoints.Training.Definitions.V1.Validators;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Training.V1;

public sealed class DeleteTrainingDatasetRequestValidator : Validator<DeleteTrainingDatasetRequest>
{
    public DeleteTrainingDatasetRequestValidator() =>
        ExpectedVersionRule.Require(this, static request => request.ExpectedVersion, "expectedVersion", "dataset");
}

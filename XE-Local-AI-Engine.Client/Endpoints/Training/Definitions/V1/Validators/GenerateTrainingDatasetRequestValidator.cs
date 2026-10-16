namespace XE_Local_AI_Engine.Client.Endpoints.Training.Definitions.V1.Validators;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Training.V1;

public sealed class GenerateTrainingDatasetRequestValidator : Validator<GenerateTrainingDatasetRequest>
{
    public GenerateTrainingDatasetRequestValidator() =>
        ExpectedVersionRule.Require(this, static request => request.ExpectedVersion, "expectedVersion", "definition");
}

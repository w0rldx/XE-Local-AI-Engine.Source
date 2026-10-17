namespace XE_Local_AI_Engine.Client.Endpoints.Training.Exports.V1.Validators;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;

public sealed class DeleteTrainingArtifactRequestValidator : Validator<DeleteTrainingArtifactRequest>
{
    public DeleteTrainingArtifactRequestValidator() =>
        ExpectedVersionRule.Require(this, static request => request.ExpectedVersion, "expectedVersion", "artifact");
}

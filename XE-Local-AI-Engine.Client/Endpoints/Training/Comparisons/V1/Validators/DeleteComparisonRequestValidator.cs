namespace XE_Local_AI_Engine.Client.Endpoints.Training.Comparisons.V1.Validators;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;

public sealed class DeleteComparisonRequestValidator : Validator<DeleteComparisonRequest>
{
    public DeleteComparisonRequestValidator() =>
        ExpectedVersionRule.Require(this, static request => request.ExpectedVersion, "expectedVersion", "comparison");
}

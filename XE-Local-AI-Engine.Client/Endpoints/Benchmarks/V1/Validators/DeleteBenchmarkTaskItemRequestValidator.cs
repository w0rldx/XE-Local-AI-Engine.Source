namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.Validators;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;

public sealed class DeleteBenchmarkTaskItemRequestValidator : Validator<DeleteBenchmarkTaskItemRequest>
{
    public DeleteBenchmarkTaskItemRequestValidator() =>
        ExpectedVersionRule.Require(this, static request => request.ExpectedVersion, "expectedVersion", "item");
}

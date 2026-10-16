namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.Validators;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;

public sealed class StartBenchmarkRunBatchRequestValidator : Validator<StartBenchmarkRunBatchRequest>
{
    public StartBenchmarkRunBatchRequestValidator() =>
        ExpectedVersionRule.Require(this, static request => request.ExpectedProjectVersion, "expectedProjectVersion", "project");
}

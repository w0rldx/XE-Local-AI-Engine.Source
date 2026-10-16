namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.Validators;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;

public sealed class ScoreBenchmarkRunRequestValidator : Validator<ScoreBenchmarkRunRequest>
{
    public ScoreBenchmarkRunRequestValidator() =>
        ExpectedVersionRule.Require(this, static request => request.ExpectedVersion, "expectedVersion", "run");
}

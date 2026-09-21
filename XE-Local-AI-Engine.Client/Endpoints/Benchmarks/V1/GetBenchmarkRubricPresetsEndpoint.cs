namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Benchmarks;

/// <summary>The rubrics the judge-policy form offers, so the UI never carries a second copy of the wording.</summary>
public sealed class GetBenchmarkRubricPresetsEndpoint : EndpointWithoutRequest<BenchmarkRubricPresetsResponse>
{
    public override void Configure()
    {
        Get(LocalApiRoutes.Benchmarks.RubricPresets);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override Task HandleAsync(CancellationToken ct) =>
        Send.OkAsync(new BenchmarkRubricPresetsResponse
        {
            Default = BenchmarkJudgeRubricDefaults.Default().ToDto(),
            Programming = BenchmarkJudgeRubricDefaults.Programming().ToDto(),
            Reasoning = BenchmarkJudgeRubricDefaults.Reasoning().ToDto(),
            Verifiable = BenchmarkJudgeRubricDefaults.Verifiable().ToDto(),
            CodeExecution = BenchmarkJudgeRubricDefaults.CodeExecution().ToDto()
        }, ct);
}

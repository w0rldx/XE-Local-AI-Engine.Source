namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.Validators;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;

/// <summary>Refuses a run with no primary model.</summary>
/// <remarks>
///     Only this check moves: the KV-cache-type refusal beside it in the handler answers a benchmark problem-details
///     body rather than a validation failure, and the blank model was reported ahead of it, which it still is.
/// </remarks>
public sealed class StartBenchmarkRunRequestValidator : Validator<StartBenchmarkRunRequest>
{
    public StartBenchmarkRunRequestValidator()
    {
        this.AddFirstViolationRule(static request =>
            string.IsNullOrWhiteSpace(request.ModelName) ? "A primary model is required." : null);
    }
}

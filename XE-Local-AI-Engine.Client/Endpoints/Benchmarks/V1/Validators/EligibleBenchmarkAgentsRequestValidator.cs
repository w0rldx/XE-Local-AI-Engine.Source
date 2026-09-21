namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.Validators;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;

/// <summary>Refuses a blank model name before the catalog is queried.</summary>
public sealed class EligibleBenchmarkAgentsRequestValidator : Validator<EligibleBenchmarkAgentsRequest>
{
    public EligibleBenchmarkAgentsRequestValidator()
    {
        this.AddFirstViolationRule(static request =>
            string.IsNullOrWhiteSpace(request.ModelName) ? "A model name is required." : null);
    }
}

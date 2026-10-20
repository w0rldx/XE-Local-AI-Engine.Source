namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.Validators;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Persistence.Stores;

public sealed class SubmitBenchmarkRunReviewRequestValidator : Validator<SubmitBenchmarkRunReviewRequest>
{
    public SubmitBenchmarkRunReviewRequestValidator()
    {
        ExpectedVersionRule.Require(this, static request => request.ExpectedVersion, "expectedVersion", "run");
        this.AddFirstViolationRule(static request => request.Criteria?.Any(static criterion => criterion.Score is null) == true
            ? "Every criterion needs a score between 0 and 10."
            : null);
        this.AddFirstViolationRule(static request => request.Notes?.Length > BenchmarkReviewLimits.MaxNotesCharacters
            ? $"Notes must be at most {BenchmarkReviewLimits.MaxNotesCharacters} characters."
            : null);
    }
}

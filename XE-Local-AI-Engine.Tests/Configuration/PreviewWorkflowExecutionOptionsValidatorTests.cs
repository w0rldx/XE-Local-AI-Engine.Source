namespace XE_Local_AI_Engine.Tests.Configuration;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Configuration.Validation;
using XE_Local_AI_Engine.Client.Services.PreviewWorkflows;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class PreviewWorkflowExecutionOptionsValidatorTests
{
    private readonly PreviewWorkflowExecutionOptionsValidator _validator = new();

    [Test]
    public void Validate_WhenOptionsAreValid_ReturnsSuccess()
    {
        var result = _validator.Validate(name: null, new PreviewWorkflowExecutionOptions());

        AssertEx.False(result.Failed);
        AssertEx.True(result.Failures is null || !result.Failures.Any());
    }

    [Test]
    public void Validate_WhenIdleTimeoutIsNotPositive_ReturnsFailure()
    {
        var result = _validator.Validate(name: null, Valid(o => o.IdleTimeout = TimeSpan.Zero));

        AssertFailureContains(result, "IdleTimeout");
    }

    [Test]
    public void Validate_WhenMaxRunDurationIsNotPositive_ReturnsFailure()
    {
        var result = _validator.Validate(name: null, Valid(o => o.MaxRunDuration = TimeSpan.FromSeconds(-1)));

        AssertFailureContains(result, "MaxRunDuration");
    }

    [Test]
    public void Validate_WhenSweepIntervalIsNotPositive_ReturnsFailure()
    {
        var result = _validator.Validate(name: null, Valid(o => o.SweepInterval = TimeSpan.Zero));

        AssertFailureContains(result, "SweepInterval");
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    public void Validate_WhenMaxConcurrentRunsIsNotPositive_ReturnsFailure(int value)
    {
        var result = _validator.Validate(name: null, Valid(o => o.MaxConcurrentRuns = value));

        AssertFailureContains(result, "MaxConcurrentRuns");
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    public void Validate_WhenMaxOutputBytesIsNotPositive_ReturnsFailure(int value)
    {
        var result = _validator.Validate(name: null, Valid(o => o.MaxOutputBytes = value));

        AssertFailureContains(result, "MaxOutputBytes");
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    public void Validate_WhenMaxBufferedEventsPerRunIsNotPositive_ReturnsFailure(int value)
    {
        var result = _validator.Validate(name: null, Valid(o => o.MaxBufferedEventsPerRun = value));

        AssertFailureContains(result, "MaxBufferedEventsPerRun");
    }

    [Test]
    public void Validate_WhenReplayRetentionIsNegative_ReturnsFailure()
    {
        var result = _validator.Validate(name: null, Valid(o => o.ReplayRetention = TimeSpan.FromSeconds(-1)));

        AssertFailureContains(result, "ReplayRetention");
    }

    [Test]
    public void Validate_WhenReplayRetentionIsZero_ReturnsSuccess()
    {
        // Zero retention evicts a terminal log on the next sweep (no late-subscriber replay) — valid, not an error.
        var result = _validator.Validate(name: null, Valid(o => o.ReplayRetention = TimeSpan.Zero));

        AssertEx.False(result.Failed);
    }

    private static PreviewWorkflowExecutionOptions Valid(Action<PreviewWorkflowExecutionOptions> mutate)
    {
        var options = new PreviewWorkflowExecutionOptions();
        mutate(options);
        return options;
    }

    private static void AssertFailureContains(ValidateOptionsResult result, string expectedText)
    {
        AssertEx.False(result.Succeeded);
        AssertEx.NotEmpty(result.Failures);
        AssertEx.Contains(result.Failures, failure => failure.Contains(expectedText, StringComparison.Ordinal));
    }
}

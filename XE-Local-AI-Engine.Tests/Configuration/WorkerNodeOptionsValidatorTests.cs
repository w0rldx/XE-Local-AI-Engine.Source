namespace XE_Local_AI_Engine.Tests.Configuration;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Configuration.Validation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class WorkerNodeOptionsValidatorTests
{
    private readonly WorkerNodeOptionsValidator _validator = new();

    [Test]
    public void Validate_WhenOptionsAreValid_ReturnsSuccess()
    {
        var result = _validator.Validate(name: null, CreateValidOptions());

        AssertEx.False(result.Failed);
        AssertEx.True(result.Failures is null || !result.Failures.Any());
    }

    [Test]
    public void Defaults_UseTenMinutePendingToolCallAge()
    {
        var options = CreateValidOptions();

        AssertEx.Equal(expected: 10, options.MaxPendingToolCallAgeMinutes);
    }

    [Test]
    public void Defaults_UseFiveMinuteDisconnectGrace()
    {
        AssertEx.Equal(expected: 300, CreateValidOptions().DetachedGraceSeconds);
    }

    [Test]
    public void Validate_WhenDetachedGraceIsNegative_ReturnsFailure()
    {
        // 0 is legal (never cancel); negative is not, and must be rejected at the boundary rather than reaching the
        // reaper as an always-expired grace that would cancel every detached run on its first tick.
        var options = CreateValidOptions();
        options.DetachedGraceSeconds = -1;

        var result = _validator.Validate(name: null, options);

        AssertFailureContains(result, "DetachedGraceSeconds");
    }

    [Test]
    public void Validate_WhenDetachedGraceIsZero_ReturnsSuccess()
    {
        var options = CreateValidOptions();
        options.DetachedGraceSeconds = 0;

        var result = _validator.Validate(name: null, options);

        AssertEx.False(result.Failed, "0 disables reaping and is a supported operator choice");
    }

    [Test]
    public void Validate_WhenNodeNameIsMissing_ReturnsFailure()
    {
        var options = CreateValidOptions();
        options.NodeName = string.Empty;

        var result = _validator.Validate(name: null, options);

        AssertFailureContains(result, "NodeName");
    }

    [Test]
    public void Validate_WhenMaxResponseSizeBelowMinimum_ReturnsFailure()
    {
        var options = CreateValidOptions();
        options.MaxResponseSizeMb = 0;

        var result = _validator.Validate(name: null, options);

        AssertFailureContains(result, "MaxResponseSizeMb");
    }

    [Test]
    public void Validate_WhenMaxPendingToolCallAgeBelowMinimum_ReturnsFailure()
    {
        var options = CreateValidOptions();
        options.MaxPendingToolCallAgeMinutes = 0;

        var result = _validator.Validate(name: null, options);

        AssertFailureContains(result, "MaxPendingToolCallAgeMinutes");
    }

    [Test]
    public void Validate_WhenMaxPendingToolCallAgeAboveMaximum_ReturnsFailure()
    {
        var options = CreateValidOptions();
        options.MaxPendingToolCallAgeMinutes = 61;

        var result = _validator.Validate(name: null, options);

        AssertFailureContains(result, "MaxPendingToolCallAgeMinutes");
    }

    private static WorkerNodeOptions CreateValidOptions()
    {
        return new WorkerNodeOptions
        {
            NodeName = "worker-node-test"
        };
    }

    private static void AssertFailureContains(ValidateOptionsResult result, string expectedText)
    {
        AssertEx.False(result.Succeeded);
        AssertEx.NotEmpty(result.Failures);
        AssertEx.Contains(result.Failures, failure => failure.Contains(expectedText, StringComparison.Ordinal));
    }
}

namespace XE_Local_AI_Engine.Tests.Configuration;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Configuration;
using XE_Local_AI_Engine.Configuration.Validation;
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
            NodeName = "worker-node-test",
        };
    }

    private static void AssertFailureContains(ValidateOptionsResult result, string expectedText)
    {
        AssertEx.False(result.Succeeded);
        AssertEx.NotEmpty(result.Failures);
        AssertEx.Contains(result.Failures, failure => failure.Contains(expectedText, StringComparison.Ordinal));
    }
}

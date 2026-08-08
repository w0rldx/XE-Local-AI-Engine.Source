namespace XE_Local_AI_Engine.Tests.Configuration;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Configuration.Validation;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class SpawnOptionsValidatorTests
{
    private readonly SpawnOptionsValidator _validator = new();

    [Test]
    public void Validate_WhenOptionsAreValid_ReturnsSuccess()
    {
        var result = _validator.Validate(name: null, new SpawnOptions());

        AssertEx.False(result.Failed);
        AssertEx.True(result.Failures is null || !result.Failures.Any());
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    public void Validate_WhenMaxConcurrentSpawnsIsNotPositive_ReturnsFailure(int value)
    {
        var result = _validator.Validate(name: null, new SpawnOptions
        {
            MaxConcurrentSpawns = value
        });

        AssertFailureContains(result, "MaxConcurrentSpawns");
    }

    [Test]
    [Arguments(-1)]
    [Arguments(-100)]
    public void Validate_WhenMaxCloudSpawnsIsNegative_ReturnsFailure(int value)
    {
        var result = _validator.Validate(name: null, new SpawnOptions
        {
            MaxCloudSpawns = value
        });

        AssertFailureContains(result, "MaxCloudSpawns");
    }

    [Test]
    public void Validate_WhenMaxCloudSpawnsIsZero_ReturnsSuccess()
    {
        // Zero cloud spawns is a valid "cloud disabled" configuration, not an error.
        var result = _validator.Validate(name: null, new SpawnOptions
        {
            MaxCloudSpawns = 0
        });

        AssertEx.False(result.Failed);
    }

    [Test]
    [Arguments(-1)]
    public void Validate_WhenQueueWaitSecondsIsNegative_ReturnsFailure(int value)
    {
        var result = _validator.Validate(name: null, new SpawnOptions
        {
            QueueWaitSeconds = value
        });

        AssertFailureContains(result, "QueueWaitSeconds");
    }

    [Test]
    public void Validate_WhenQueueWaitSecondsIsZero_ReturnsSuccess()
    {
        // Zero wait means reject a busy same-model turn immediately — valid.
        var result = _validator.Validate(name: null, new SpawnOptions
        {
            QueueWaitSeconds = 0
        });

        AssertEx.False(result.Failed);
    }

    private static void AssertFailureContains(ValidateOptionsResult result, string expectedText)
    {
        AssertEx.False(result.Succeeded);
        AssertEx.NotEmpty(result.Failures);
        AssertEx.Contains(result.Failures, failure => failure.Contains(expectedText, StringComparison.Ordinal));
    }
}

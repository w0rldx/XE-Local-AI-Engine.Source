namespace XE_Local_AI_Engine.Tests.Configuration;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Configuration;
using XE_Local_AI_Engine.Configuration.Validation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class CentralPlatformOptionsValidatorTests
{
    private readonly CentralPlatformOptionsValidator _validator = new();

    [Test]
    public void Validate_WhenOptionsAreValid_ReturnsSuccess()
    {
        var result = _validator.Validate(name: null, CreateValidOptions());

        AssertEx.False(result.Failed);
        AssertEx.True(result.Failures is null || !result.Failures.Any());
    }

    [Test]
    public void Validate_WhenBaseUrlIsMissing_ReturnsFailure()
    {
        var options = CreateValidOptions();
        options.BaseUrl = string.Empty;

        var result = _validator.Validate(name: null, options);

        AssertFailureContains(result, "BaseUrl");
    }

    [Test]
    public void Validate_WhenHeartbeatBelowMinimum_ReturnsFailure()
    {
        var options = CreateValidOptions();
        options.HeartbeatIntervalSeconds = 4;

        var result = _validator.Validate(name: null, options);

        AssertFailureContains(result, "HeartbeatIntervalSeconds");
    }

    [Test]
    public void Validate_WhenHeartbeatAboveMaximum_ReturnsFailure()
    {
        var options = CreateValidOptions();
        options.HeartbeatIntervalSeconds = 301;

        var result = _validator.Validate(name: null, options);

        AssertFailureContains(result, "HeartbeatIntervalSeconds");
    }

    [Test]
    public void Validate_WhenMaxReconnectAttemptsIsZero_ReturnsFailure()
    {
        var options = CreateValidOptions();
        options.MaxReconnectAttempts = 0;

        var result = _validator.Validate(name: null, options);

        AssertFailureContains(result, "MaxReconnectAttempts");
    }

    [Test]
    public void Validate_WhenMaxReconnectAttemptsExceedsLimit_ReturnsFailure()
    {
        var options = CreateValidOptions();
        options.MaxReconnectAttempts = 101;

        var result = _validator.Validate(name: null, options);

        AssertFailureContains(result, "MaxReconnectAttempts");
    }

    [Test]
    public void Validate_WhenMessageSizeBelowMinimum_ReturnsFailure()
    {
        var options = CreateValidOptions();
        options.MaxSignalRMessageSizeKb = 15;

        var result = _validator.Validate(name: null, options);

        AssertFailureContains(result, "MaxSignalRMessageSizeKb");
    }

    [Test]
    public void Validate_WhenMessageSizeAboveMaximum_ReturnsFailure()
    {
        var options = CreateValidOptions();
        options.MaxSignalRMessageSizeKb = 1025;

        var result = _validator.Validate(name: null, options);

        AssertFailureContains(result, "MaxSignalRMessageSizeKb");
    }

    private static CentralPlatformOptions CreateValidOptions()
    {
        return new CentralPlatformOptions
        {
            BaseUrl = "https://test.example.com",
        };
    }

    private static void AssertFailureContains(ValidateOptionsResult result, string expectedText)
    {
        AssertEx.False(result.Succeeded);
        AssertEx.NotEmpty(result.Failures);
        AssertEx.Contains(result.Failures, failure => failure.Contains(expectedText, StringComparison.Ordinal));
    }
}

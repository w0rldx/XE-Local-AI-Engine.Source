namespace XE_Local_AI_Engine.Tests.Configuration;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Configuration.Validation;
using XE_Local_AI_Engine.Client.Services.Scheduler;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class SchedulerOptionsValidatorTests
{
    private readonly SchedulerOptionsValidator _validator = new();

    [Test]
    public void Validate_WhenOptionsAreValid_ReturnsSuccess()
    {
        var result = _validator.Validate(null, CreateValidOptions());

        AssertEx.False(result.Failed);
        AssertEx.True(result.Failures is null || !result.Failures.Any());
    }

    [Test]
    public void Defaults_HaveExpectedValues()
    {
        var options = new SchedulerOptions();

        AssertEx.True(options.Enabled, "Enabled should default to true.");
        AssertEx.Equal(4, options.MaxConcurrency);
        AssertEx.Equal(30, options.HistoryRetentionDays);
        AssertEx.Equal(60, options.RetentionSweepIntervalMinutes);
        AssertEx.Equal("UTC", options.DefaultTimeZoneId);
        AssertEx.Equal(5, options.DefaultMaxRuntimeMinutes);
        AssertEx.Equal("QRTZ_", options.QuartzTablePrefix);
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    [Arguments(-100)]
    public void Validate_WhenMaxConcurrencyIsNotPositive_ReturnsFailure(int value)
    {
        var options = new SchedulerOptions
        {
            MaxConcurrency = value
        };

        var result = _validator.Validate(null, options);

        AssertFailureContains(result, "MaxConcurrency");
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    public void Validate_WhenHistoryRetentionDaysIsNotPositive_ReturnsFailure(int value)
    {
        var options = new SchedulerOptions
        {
            HistoryRetentionDays = value
        };

        var result = _validator.Validate(null, options);

        AssertFailureContains(result, "HistoryRetentionDays");
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    public void Validate_WhenRetentionSweepIntervalMinutesIsNotPositive_ReturnsFailure(int value)
    {
        var options = new SchedulerOptions
        {
            RetentionSweepIntervalMinutes = value
        };

        var result = _validator.Validate(null, options);

        AssertFailureContains(result, "RetentionSweepIntervalMinutes");
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    public void Validate_WhenDefaultMaxRuntimeMinutesIsNotPositive_ReturnsFailure(int value)
    {
        var options = new SchedulerOptions
        {
            DefaultMaxRuntimeMinutes = value
        };

        var result = _validator.Validate(null, options);

        AssertFailureContains(result, "DefaultMaxRuntimeMinutes");
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    public void Validate_WhenDefaultTimeZoneIdIsBlank_ReturnsFailure(string value)
    {
        var options = new SchedulerOptions
        {
            DefaultTimeZoneId = value
        };

        var result = _validator.Validate(null, options);

        AssertFailureContains(result, "DefaultTimeZoneId");
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    public void Validate_WhenQuartzTablePrefixIsBlank_ReturnsFailure(string value)
    {
        var options = new SchedulerOptions
        {
            QuartzTablePrefix = value
        };

        var result = _validator.Validate(null, options);

        AssertFailureContains(result, "QuartzTablePrefix");
    }

    [Test]
    public void Validate_WhenMultipleFieldsAreInvalid_ReportsAllFailures()
    {
        var options = new SchedulerOptions
        {
            MaxConcurrency = 0,
            HistoryRetentionDays = -1,
            DefaultTimeZoneId = ""
        };

        var result = _validator.Validate(null, options);

        AssertEx.False(result.Succeeded);
        var failures = result.Failures?.ToArray() ?? [];
        AssertEx.True(failures.Length >= 3, "All invalid fields should produce a failure message.");
    }

    private static SchedulerOptions CreateValidOptions()
    {
        return new SchedulerOptions();
    }

    private static void AssertFailureContains(ValidateOptionsResult result, string expectedText)
    {
        AssertEx.False(result.Succeeded);
        AssertEx.NotEmpty(result.Failures);
        AssertEx.Contains(result.Failures, failure => failure.Contains(expectedText, StringComparison.Ordinal));
    }
}

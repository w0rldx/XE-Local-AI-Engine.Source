namespace XE_Local_AI_Engine.Tests.Configuration;

using System.Globalization;
using XE_Local_AI_Engine.Client.Configuration.Validation;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Unit)]
public sealed class AgentHomeRunRetentionOptionsValidatorTests
{
    private readonly AgentHomeRunRetentionOptionsValidator _validator = new();

    [Test]
    public void Validate_WhenOptionsAreTheShippedDefaults_ReturnsSuccess()
    {
        var result = _validator.Validate(name: null, new AgentHomeRunRetentionOptions());

        AssertEx.False(result.Failed);
    }

    /// <summary>Zero is the documented "this limit is off" sentinel, one limit at a time and all three together.</summary>
    [Test]
    [Arguments(0, 200, 2147483648L)]
    [Arguments(30, 0, 2147483648L)]
    [Arguments(30, 200, 0L)]
    [Arguments(0, 0, 1L)]
    public void Validate_WhenALimitIsTurnedOffWithZero_ReturnsSuccess(int retentionDays, int maxRuns, long maxTotalBytes)
    {
        var result = _validator.Validate(name: null, new AgentHomeRunRetentionOptions
        {
            RetentionDays = retentionDays,
            MaxRuns = maxRuns,
            MaxTotalBytes = maxTotalBytes
        });

        AssertEx.False(result.Failed);
    }

    [Test]
    [Arguments(-1, "RetentionDays", "a negative age limit")]
    [Arguments(3651, "RetentionDays", "an age limit past the upper bound")]
    public void Validate_WhenRetentionDaysIsOutOfRange_NamesTheKeyAndTheValue(int retentionDays, string key, string why)
    {
        AssertFailureNames(new AgentHomeRunRetentionOptions
            {
                RetentionDays = retentionDays
            },
            key,
            retentionDays.ToString(CultureInfo.InvariantCulture),
            why);
    }

    [Test]
    [Arguments(-1, "MaxRuns", "a negative run cap")]
    [Arguments(100001, "MaxRuns", "a run cap past the upper bound")]
    public void Validate_WhenMaxRunsIsOutOfRange_NamesTheKeyAndTheValue(int maxRuns, string key, string why)
    {
        AssertFailureNames(new AgentHomeRunRetentionOptions
            {
                MaxRuns = maxRuns
            },
            key,
            maxRuns.ToString(CultureInfo.InvariantCulture),
            why);
    }

    [Test]
    [Arguments(-1L, "MaxTotalBytes", "a negative byte cap")]
    [Arguments(1099511627777L, "MaxTotalBytes", "a byte cap past the upper bound")]
    public void Validate_WhenMaxTotalBytesIsOutOfRange_NamesTheKeyAndTheValue(long maxTotalBytes, string key, string why)
    {
        AssertFailureNames(new AgentHomeRunRetentionOptions
            {
                MaxTotalBytes = maxTotalBytes
            },
            key,
            maxTotalBytes.ToString(CultureInfo.InvariantCulture),
            why);
    }

    [Test]
    public void Validate_WhenTheSweepIntervalIsTooShort_NamesTheKeyAndTheValue()
    {
        AssertFailureNames(new AgentHomeRunRetentionOptions
            {
                SweepInterval = TimeSpan.FromSeconds(1)
            },
            "SweepInterval",
            "00:00:01",
            "a cadence that would sweep the disk every second");
    }

    [Test]
    public void Validate_WhenTheSweepIntervalIsLongerThanAWeek_ReturnsFailure()
    {
        var result = _validator.Validate(name: null, new AgentHomeRunRetentionOptions
        {
            SweepInterval = TimeSpan.FromDays(8)
        });

        AssertEx.True(result.Failed, "a sweep that runs less than weekly is retention in name only.");
    }

    /// <summary>
    ///     Enabled with every limit off is the one combination that reads as retention while deleting nothing, so it
    ///     is refused rather than silently accepted.
    /// </summary>
    [Test]
    public void Validate_WhenEnabledWithEveryLimitOff_ReturnsFailure()
    {
        var result = _validator.Validate(name: null, new AgentHomeRunRetentionOptions
        {
            Enabled = true,
            RetentionDays = 0,
            MaxRuns = 0,
            MaxTotalBytes = 0
        });

        AssertEx.True(result.Failed);
        AssertEx.Contains(result.Failures, static failure => failure.Contains("Enabled to false", StringComparison.Ordinal));
    }

    [Test]
    public void Validate_WhenDisabledWithEveryLimitOff_ReturnsSuccess()
    {
        var result = _validator.Validate(name: null, new AgentHomeRunRetentionOptions
        {
            Enabled = false,
            RetentionDays = 0,
            MaxRuns = 0,
            MaxTotalBytes = 0
        });

        AssertEx.False(result.Failed, "turning the sweep off is the honest way to disable retention.");
    }

    private void AssertFailureNames(AgentHomeRunRetentionOptions options, string key, string value, string why)
    {
        var result = _validator.Validate(name: null, options);

        AssertEx.True(result.Failed, why);
        AssertEx.Contains(result.Failures,
            failure => failure.Contains($"AgentHome:RunRetention:{key}", StringComparison.Ordinal)
                       && failure.Contains(value, StringComparison.Ordinal),
            $"the message must name the key AND the value the operator wrote ({why}).");
    }
}

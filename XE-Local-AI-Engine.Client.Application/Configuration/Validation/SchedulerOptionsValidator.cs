namespace XE_Local_AI_Engine.Client.Configuration.Validation;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Scheduler;

/// <summary>
///     Startup/options validator for scheduler options settings.
/// </summary>
public sealed class SchedulerOptionsValidator : IValidateOptions<SchedulerOptions>
{
    public ValidateOptionsResult Validate(string? name, SchedulerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var errors = Enumerable.Empty<string>()
                               .AppendIf(options.MaxConcurrency <= 0,
                                   "Scheduler:MaxConcurrency must be greater than zero.")
                               .AppendIf(options.HistoryRetentionDays <= 0,
                                   "Scheduler:HistoryRetentionDays must be greater than zero.")
                               .AppendIf(options.RetentionSweepIntervalMinutes <= 0,
                                   "Scheduler:RetentionSweepIntervalMinutes must be greater than zero.")
                               .AppendIf(options.DefaultMaxRuntimeMinutes <= 0,
                                   "Scheduler:DefaultMaxRuntimeMinutes must be greater than zero.")
                               .AppendIf(string.IsNullOrWhiteSpace(options.DefaultTimeZoneId),
                                   "Scheduler:DefaultTimeZoneId must not be null or whitespace.")
                               .AppendIf(string.IsNullOrWhiteSpace(options.QuartzTablePrefix),
                                   "Scheduler:QuartzTablePrefix must not be null or whitespace.")
                               .ToArray();

        return errors.Length == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

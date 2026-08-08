namespace XE_Local_AI_Engine.Client.Configuration.Validation;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.PreviewWorkflows;

/// <summary>
///     Makes the <see cref="PreviewWorkflowExecutionOptions" /> <c>ValidateOnStart</c> registration authoritative: the
///     options module wired the fail-fast hook but registered no validator, so an out-of-range cap (a zero sweep
///     interval that breaks the periodic timer, a non-positive concurrency/byte cap) bound silently. Every duration and
///     count that bounds a run must be positive; the replay-retention window alone may be zero (evict a terminal log on
///     the next sweep, forgoing late-subscriber replay) but never negative.
/// </summary>
public sealed class PreviewWorkflowExecutionOptionsValidator : IValidateOptions<PreviewWorkflowExecutionOptions>
{
    public ValidateOptionsResult Validate(string? name, PreviewWorkflowExecutionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var errors = Enumerable.Empty<string>()
                               .AppendIf(options.IdleTimeout <= TimeSpan.Zero,
                                   "PreviewWorkflows:Execution:IdleTimeout must be greater than zero.")
                               .AppendIf(options.MaxRunDuration <= TimeSpan.Zero,
                                   "PreviewWorkflows:Execution:MaxRunDuration must be greater than zero.")
                               .AppendIf(options.SweepInterval <= TimeSpan.Zero,
                                   "PreviewWorkflows:Execution:SweepInterval must be greater than zero.")
                               .AppendIf(options.AbandonedSubscriberGrace <= TimeSpan.Zero,
                                   "PreviewWorkflows:Execution:AbandonedSubscriberGrace must be greater than zero.")
                               .AppendIf(options.MaxConcurrentRuns <= 0,
                                   "PreviewWorkflows:Execution:MaxConcurrentRuns must be greater than zero.")
                               .AppendIf(options.MaxOutputBytes <= 0,
                                   "PreviewWorkflows:Execution:MaxOutputBytes must be greater than zero.")
                               .AppendIf(options.MaxBufferedEventsPerRun <= 0,
                                   "PreviewWorkflows:Execution:MaxBufferedEventsPerRun must be greater than zero.")
                               .AppendIf(options.ReplayRetention < TimeSpan.Zero,
                                   "PreviewWorkflows:Execution:ReplayRetention must be zero or greater.")
                               .ToArray();

        return errors.Length == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

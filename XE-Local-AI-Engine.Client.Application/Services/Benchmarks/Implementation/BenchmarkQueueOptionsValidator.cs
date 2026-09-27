namespace XE_Local_AI_Engine.Client.Services.Benchmarks.Implementation;

using Microsoft.Extensions.Options;

/// <summary>
///     Fails the node's start rather than the first poll: the hosted service already refused a non-positive interval,
///     but it did so from a background thread after boot, where the operator sees a log line instead of a failure.
/// </summary>
internal sealed class BenchmarkQueueOptionsValidator : IValidateOptions<BenchmarkQueueOptions>
{
    public ValidateOptionsResult Validate(string? name, BenchmarkQueueOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.PollInterval > TimeSpan.Zero && options.PollInterval <= BenchmarkQueueOptions.MaxPollInterval
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail($"{BenchmarkQueueOptions.SectionName}:PollInterval must be positive and at most "
                                         + $"{BenchmarkQueueOptions.MaxPollInterval}.");
    }
}

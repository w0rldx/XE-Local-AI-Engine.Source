namespace XE_Local_AI_Engine.Client.Configuration.Validation;

using System.Globalization;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.AgentHome;

/// <summary>
///     Bounds on the AgentHome run-retention limits. Zero is the documented "this limit is off" sentinel for each of
///     the three, so only a negative value is an error.
/// </summary>
/// <remarks>
///     The upper bounds exist because a limit set so large it can never bite reads as retention that is on while
///     behaving as if it were off.
/// </remarks>
public sealed class AgentHomeRunRetentionOptionsValidator : IValidateOptions<AgentHomeRunRetentionOptions>
{
    private const int MaxRetentionDays = 3650;
    private const int MaxRunCap = 100000;
    private const long MaxByteCap = 1099511627776;
    private static readonly TimeSpan MinSweepInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxSweepInterval = TimeSpan.FromDays(7);

    public ValidateOptionsResult Validate(string? name, AgentHomeRunRetentionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var errors = Enumerable.Empty<string>()
                               .AppendIf(options.RetentionDays < 0 || options.RetentionDays > MaxRetentionDays,
                                   Invariant($"AgentHome:RunRetention:RetentionDays ({options.RetentionDays}) must be between 0 and {MaxRetentionDays}; 0 turns the age limit off."))
                               .AppendIf(options.MaxRuns < 0 || options.MaxRuns > MaxRunCap,
                                   Invariant($"AgentHome:RunRetention:MaxRuns ({options.MaxRuns}) must be between 0 and {MaxRunCap}; 0 turns the count limit off."))
                               .AppendIf(options.MaxTotalBytes < 0 || options.MaxTotalBytes > MaxByteCap,
                                   Invariant($"AgentHome:RunRetention:MaxTotalBytes ({options.MaxTotalBytes}) must be between 0 and {MaxByteCap}; 0 turns the byte limit off."))
                               .AppendIf(options.SweepInterval < MinSweepInterval || options.SweepInterval > MaxSweepInterval,
                                   Invariant($"AgentHome:RunRetention:SweepInterval ({options.SweepInterval}) must be between {MinSweepInterval} and {MaxSweepInterval}."))
                               .AppendIf(options.Enabled
                                   && options.RetentionDays == 0
                                   && options.MaxRuns == 0
                                   && options.MaxTotalBytes == 0,
                                   "AgentHome:RunRetention is enabled but RetentionDays, MaxRuns and MaxTotalBytes are all 0, "
                                   + "so the sweep would delete nothing. Set one of them, or set AgentHome:RunRetention:Enabled to false.")
                               .ToArray();

        return errors.Length == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private static string Invariant(FormattableString message) =>
        message.ToString(CultureInfo.InvariantCulture);
}

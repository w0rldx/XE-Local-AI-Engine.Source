namespace XE_Local_AI_Engine.Client.BackgroundServices;

using System.ComponentModel.DataAnnotations;

/// <summary>
///     Policy for the chat retention sweeper, which deletes whole conversations (and their complete on-disk + DB
///     footprint) once they age past <see cref="RetentionDays" />. Retention is a data-minimization feature that
///     permanently destroys user chat history, so it ships <b>disabled by default</b> and must be explicitly turned on
///     via configuration. Bound from the <see cref="Section" /> configuration section and validated on startup
///     (<c>ValidateDataAnnotations().ValidateOnStart()</c>) so a hostile/typo'd window can never silently delete
///     everything: the sweep cutoff is <c>now - RetentionDays</c>, so a zero or negative window would set a cutoff at or
///     after "now" and purge every conversation the instant retention is enabled.
/// </summary>
/// <remarks>
///     UI exposure of this policy is a follow-up: today it is configured only at the appsettings level.
/// </remarks>
public sealed class ChatRetentionOptions : IValidatableObject
{
    public const string Section = "ChatRetention";

    /// <summary>The narrowest sweep interval allowed: below this the sweeper would busy-spin.</summary>
    private static readonly TimeSpan MinSweepInterval = TimeSpan.FromSeconds(1);

    /// <summary>The widest sweep interval allowed; also within <see cref="PeriodicTimer" />'s supported range.</summary>
    private static readonly TimeSpan MaxSweepInterval = TimeSpan.FromDays(7);

    /// <summary>
    ///     Whether the retention sweep runs at all. Default <c>false</c>: no conversation is ever auto-deleted unless an
    ///     operator opts in. When <c>false</c> the background service does not delete conversations (it logs once at
    ///     startup); the orphaned-upload resweep still runs regardless, so a stranded upload directory is reconciled.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    ///     Age threshold in days. When <see cref="Enabled" />, a conversation whose <c>last_seen_utc</c> is older than
    ///     now minus this window (or that is soft-purged) is permanently deleted. Must be at least 1: the cutoff is
    ///     <c>now - RetentionDays</c>, so 0 or a negative value would purge every conversation immediately. Default 30.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int RetentionDays { get; set; } = 30;

    /// <summary>How often the sweep runs while enabled. Must be in <c>[1s, 7d]</c>.</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromMinutes(10);

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (SweepInterval < MinSweepInterval || SweepInterval > MaxSweepInterval)
        {
            yield return new ValidationResult(
                $"{Section}:{nameof(SweepInterval)} must be between {MinSweepInterval} and {MaxSweepInterval}.",
                [nameof(SweepInterval)]);
        }
    }
}

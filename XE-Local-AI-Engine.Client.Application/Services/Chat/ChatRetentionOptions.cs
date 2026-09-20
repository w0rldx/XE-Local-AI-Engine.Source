namespace XE_Local_AI_Engine.Client.Services.Chat;

using System.ComponentModel.DataAnnotations;

/// <summary>
///     Policy for the chat retention sweeper, which deletes whole conversations and their complete on-disk and
///     database footprint once they age past <see cref="RetentionDays" />.
/// </summary>
/// <remarks>
///     Retention permanently destroys user chat history, so it ships <b>disabled by default</b> and is turned on only
///     through the <see cref="Section" /> configuration section, which the UI does not expose. It is validated on
///     startup because the cutoff is <c>now - RetentionDays</c>, so a zero or negative window would purge every
///     conversation the instant retention is enabled.
/// </remarks>
public sealed class ChatRetentionOptions : IValidatableObject
{
    public const string Section = "ChatRetention";

    /// <summary>The narrowest sweep interval allowed: below this the sweeper would busy-spin.</summary>
    private static readonly TimeSpan MinSweepInterval = TimeSpan.FromSeconds(1);

    /// <summary>The widest sweep interval allowed; also within <see cref="PeriodicTimer" />'s supported range.</summary>
    private static readonly TimeSpan MaxSweepInterval = TimeSpan.FromDays(7);

    /// <summary>
    ///     Whether the retention sweep runs at all; <c>false</c> by default, so nothing is auto-deleted.
    /// </summary>
    /// <remarks>
    ///     When <c>false</c> the background service logs once at startup and deletes no conversation, but the
    ///     orphaned-upload resweep still runs, so a stranded upload directory is reconciled either way.
    /// </remarks>
    public bool Enabled { get; set; }

    /// <summary>
    ///     Age threshold in days, default 30: when <see cref="Enabled" />, a conversation older than that window, or
    ///     soft-purged, is permanently deleted.
    /// </summary>
    /// <remarks>It must be at least 1, or the <c>now - RetentionDays</c> cutoff purges everything immediately.</remarks>
    [Range(1, int.MaxValue)]
    public int RetentionDays { get; set; } = 30;

    /// <summary>How often the sweep runs while enabled. Must be in <c>[1s, 7d]</c>.</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromMinutes(10);

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (SweepInterval < MinSweepInterval || SweepInterval > MaxSweepInterval)
        {
            yield return new ValidationResult($"{Section}:{nameof(SweepInterval)} must be between {MinSweepInterval} and {MaxSweepInterval}.",
                [nameof(SweepInterval)]);
        }
    }
}

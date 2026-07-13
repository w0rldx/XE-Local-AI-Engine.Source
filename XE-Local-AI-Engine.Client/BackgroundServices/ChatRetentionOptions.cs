namespace XE_Local_AI_Engine.Client.BackgroundServices;

/// <summary>
///     Policy for the chat retention sweeper, which deletes whole conversations (and their complete on-disk + DB
///     footprint) once they age past <see cref="RetentionDays" />. Retention is a data-minimization feature that
///     permanently destroys user chat history, so it ships <b>disabled by default</b> and must be explicitly turned on
///     via configuration. Bound from the <see cref="Section" /> configuration section.
/// </summary>
/// <remarks>
///     UI exposure of this policy is a follow-up: today it is configured only at the appsettings level.
/// </remarks>
public sealed class ChatRetentionOptions
{
    public const string Section = "ChatRetention";

    /// <summary>
    ///     Whether the retention sweep runs at all. Default <c>false</c>: no conversation is ever auto-deleted unless an
    ///     operator opts in. When <c>false</c> the background service does nothing (it logs once at startup and exits).
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    ///     Age threshold in days. When <see cref="Enabled" />, a conversation whose <c>last_seen_utc</c> is older than
    ///     now minus this window (or that is soft-purged) is permanently deleted. Default 30.
    /// </summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>How often the sweep runs while enabled.</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromMinutes(10);
}

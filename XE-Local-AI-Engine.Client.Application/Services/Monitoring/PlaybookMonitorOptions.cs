namespace XE_Local_AI_Engine.Client.Services.Monitoring;

/// <summary>
///     Options for the Playbook P5 cohort monitor. <see cref="ImprovementEpsilon" /> is the dead-band around
///     the before/after down-vote rate within which a change is treated as Flat; <see cref="MinSampleSize" /> is the
///     minimum after-enable sample count below which the verdict is InsufficientData and the action is never flagged
///     (mirrors the established feedback floor of 3).
/// </summary>
public sealed class PlaybookMonitorOptions
{
    public const string Section = "PlaybookMonitor";

    /// <summary>Dead-band on the before/after down-vote rate delta; within ±epsilon the verdict is Flat.</summary>
    public double ImprovementEpsilon { get; set; } = 0.05d;

    /// <summary>Minimum after-enable samples required before any verdict (or flag) is drawn.</summary>
    public int MinSampleSize { get; set; } = 3;
}

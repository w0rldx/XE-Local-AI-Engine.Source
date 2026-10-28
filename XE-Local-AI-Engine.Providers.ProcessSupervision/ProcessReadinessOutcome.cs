namespace XE_Local_AI_Engine.Providers.ProcessSupervision;

/// <summary>How a <see cref="ProcessReadinessRace" /> settled.</summary>
public enum ProcessReadinessOutcome
{
    /// <summary>The readiness probe reported ready.</summary>
    Ready,

    /// <summary>The readiness probe gave up while the child was still running.</summary>
    TimedOut,

    /// <summary>The child exited before it became ready.</summary>
    Exited,
}

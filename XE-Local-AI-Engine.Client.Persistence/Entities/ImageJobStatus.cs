namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Lifecycle status of a local image-generation job. Mirrors <c>ScheduledRunStatus</c> for the on-demand image
///     pipeline (create → status/progress → cancel → retrieve). Persisted as an <see langword="int" />.
/// </summary>
public enum ImageJobStatus
{
    /// <summary>Accepted and waiting for a generation slot (the coordinator serializes to one running job).</summary>
    Queued = 0,

    /// <summary>The sd-server daemon is actively generating this job.</summary>
    Generating = 1,

    /// <summary>Generation completed and the result image was persisted.</summary>
    Succeeded = 2,

    /// <summary>Generation failed; <c>SanitizedError</c> carries a display-safe reason.</summary>
    Failed = 3,

    /// <summary>The job was cancelled (queued → clean cancel, or generating → daemon restart).</summary>
    Cancelled = 4
}

namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

/// <summary>
///     A lease held for the lifetime of one in-flight image generation (submit → poll → complete). While at least one
///     lease is held against a daemon, the supervisor's idle reaper and cap-admission LRU evictor never tear it down —
///     even past the idle TTL. A daemon's <c>LastUsedUtc</c> is stamped per ensure/reuse, not per
///     generation step, so a single long generation legitimately outruns the idle window and would otherwise look idle to
///     the reaper, which would tree-kill it mid-image. Mirrors the llama-server inference-lease pattern.
/// </summary>
/// <remarks>
///     Dispose the lease exactly once when the job ends (completed / failed / cancelled). <see cref="Touch" /> refreshes
///     the daemon's last-used timestamp on each poll so the idle window is measured from the last observed progress, not
///     from job submission.
/// </remarks>
public interface IImageServerJobLease : IDisposable
{
    /// <summary>Refreshes the leased daemon's last-used timestamp so a long generation never drifts past the idle window.</summary>
    void Touch();
}

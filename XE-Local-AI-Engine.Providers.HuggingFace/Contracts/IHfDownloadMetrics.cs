namespace XE_Local_AI_Engine.Providers.HuggingFace.Contracts;

/// <summary>Seam for recording Hugging Face download reliability signals.</summary>
/// <remarks>
///     The download client lives in the Providers.HuggingFace layer, which cannot reference the application layer's
///     <c>NodeMetrics</c> meter directly (layering: providers depend only on Abstractions / their own assembly). The
///     host supplies a <c>NodeMetrics</c>-backed implementation; tests and headless hosts fall back to
///     <see cref="Implementation.NullHfDownloadMetrics" />. Mirrors the established <c>IHardwareProbeMetrics</c> seam.
/// </remarks>
public interface IHfDownloadMetrics
{
    /// <summary>
    ///     Records that a download's body-copy loop stalled longer than the configured read-idle timeout and was
    ///     cancelled (surfaced as a transient failure the resume/retry path then re-attempts).
    /// </summary>
    /// <remarks>Content-free — a count only; carries no URL, repo, or file name.</remarks>
    void RecordReadIdleTimeout();
}

namespace XE_Local_AI_Engine.Providers.HuggingFace.Telemetry;

/// <summary>
///     Seam for recording Hugging Face download reliability signals (AUD4-18). The download client lives in the
///     Providers.HuggingFace layer, which cannot reference the application layer's <c>NodeMetrics</c> meter directly
///     (layering: providers depend only on Abstractions / their own assembly). The host supplies a
///     <c>NodeMetrics</c>-backed implementation; tests and headless hosts fall back to
///     <see cref="NullHfDownloadMetrics" />. Mirrors the established <c>IHardwareProbeMetrics</c> seam.
/// </summary>
public interface IHfDownloadMetrics
{
    /// <summary>
    ///     Records that a download's body-copy loop stalled longer than the configured read-idle timeout and was
    ///     cancelled (surfaced as a transient failure the resume/retry path then re-attempts). Content-free — a count
    ///     only; carries no URL, repo, or file name.
    /// </summary>
    void RecordReadIdleTimeout();
}

/// <summary>No-op <see cref="IHfDownloadMetrics" /> — the default when no metrics sink is wired.</summary>
public sealed class NullHfDownloadMetrics : IHfDownloadMetrics
{
    /// <summary>The shared no-op instance.</summary>
    public static NullHfDownloadMetrics Instance { get; } = new();

    /// <inheritdoc />
    public void RecordReadIdleTimeout()
    {
        // Intentionally does nothing — the null-object default.
    }
}

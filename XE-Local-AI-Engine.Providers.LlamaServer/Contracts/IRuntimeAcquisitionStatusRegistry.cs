namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Holds the current llama.cpp runtime acquisition status for the hydrate endpoint, stamps the monotonic
///     <see cref="RuntimeAcquisitionStatusHubEvent.Sequence" /> on every write, and owns the push throttle.
///     <para>
///         <b>Why the registry owns publishing.</b> Every status write must be sequenced AND broadcast; splitting those
///         across two collaborators would let a caller record a status that never reaches a connected client (or push
///         one that hydrate never sees). So the registry takes the <see cref="IRuntimeAcquisitionEventPublisher" /> as
///         its dependency and callers talk only to the registry.
///     </para>
///     <para>
///         <b>The registry write is always unconditional</b> — only the push is throttled — so the hydrate endpoint
///         always serves the freshest bytes even mid-throttle.
///     </para>
/// </summary>
public interface IRuntimeAcquisitionStatusRegistry
{
    /// <summary>
    ///     The current status (never <see langword="null" />; starts at <see cref="RuntimeAcquisitionPhase.Idle" /> with
    ///     sequence 0). Served by the hydrate endpoint.
    /// </summary>
    RuntimeAcquisitionStatusHubEvent Current { get; }

    /// <summary>
    ///     Records <paramref name="update" /> as the current status under a freshly-stamped sequence and broadcasts it
    ///     (subject to the byte-update throttle). Fire-and-forget and non-throwing: this is called from the download
    ///     byte loop and from the startup path, neither of which may block or fail on a push.
    /// </summary>
    void Report(RuntimeAcquisitionUpdate update);
}

/// <summary>
///     One status write. The sequence is stamped by the registry, never by the caller.
/// </summary>
/// <param name="Phase">The lifecycle stage being reported.</param>
/// <param name="Variant">The <see cref="GpuVariant" /> name, when known.</param>
/// <param name="Tag">The release tag, when resolved.</param>
/// <param name="CompletedBytes">Bytes written so far, for <see cref="RuntimeAcquisitionPhase.Downloading" />.</param>
/// <param name="TotalBytes">The total size when a <c>Content-Length</c> was supplied.</param>
/// <param name="StepIndex">1-based index of the archive being acquired.</param>
/// <param name="StepCount">Total archives this acquisition fetches.</param>
/// <param name="SanitizedError">A user-safe failure reason; only set with <see cref="RuntimeAcquisitionPhase.Failed" />.</param>
public sealed record RuntimeAcquisitionUpdate(
    RuntimeAcquisitionPhase Phase,
    string? Variant = null,
    string? Tag = null,
    long? CompletedBytes = null,
    long? TotalBytes = null,
    int StepIndex = 1,
    int StepCount = 1,
    string? SanitizedError = null);

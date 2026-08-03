namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Publishes first-run llama.cpp runtime acquisition progress (GPU probe → download → verify → extract) to connected
///     operator clients. The default implementation
///     (<see cref="Implementation.NullRuntimeAcquisitionEventPublisher" />) is a no-op; the Client host swaps in a
///     hub-backed publisher (<c>RuntimeAcquisitionEventPublisher</c> over the <c>RuntimeAcquisitionHub</c>), mirroring
///     the GGUF download and CUDA build hubs.
/// </summary>
/// <remarks>
///     <para>
///         This exists because the acquisition happens off the startup path while a fully-rendered, idle-looking UI is
///         already on screen: without a push channel a slow first-run download is indistinguishable from a broken one.
///     </para>
///     <para>
///         Payloads are sanitized at the boundary — never an absolute path, a download URL, or a token. Failure text
///         comes from <see cref="LlamaRuntimeException" />, whose messages are user-safe by contract; any other
///         exception is collapsed to a generic reason rather than surfaced verbatim.
///     </para>
/// </remarks>
public interface IRuntimeAcquisitionEventPublisher
{
    /// <summary>Pushes the latest sanitized acquisition status to all connected operator clients.</summary>
    Task PublishStatusAsync(RuntimeAcquisitionStatusHubEvent statusEvent, CancellationToken cancellationToken = default);
}

/// <summary>
///     Stable SignalR client-method name for runtime acquisition status pushes. The React client subscribes to this
///     single method and reconciles each push against the hydrate snapshot by <see cref="RuntimeAcquisitionStatusHubEvent.Sequence" />.
/// </summary>
public static class RuntimeAcquisitionHubEvents
{
    /// <summary>The client method name a runtime acquisition status push is broadcast under.</summary>
    public const string StatusChanged = "runtimeAcquisition.statusChanged";
}

/// <summary>
///     The lifecycle stage of a llama.cpp runtime acquisition. Byte progress alone is not enough to explain the wait:
///     verification and extraction of a few-hundred-MB archive are not instant, and the Windows-CUDA path downloads two
///     archives back to back — so the phase (plus the step counter on the payload) keeps the UI honest instead of
///     running 0→100 % twice with no explanation.
/// </summary>
public enum RuntimeAcquisitionPhase
{
    /// <summary>No acquisition has been attempted in this process lifetime. The initial registry state.</summary>
    Idle = 0,

    /// <summary>Indeterminate: probing for the GPU vendor to choose the runtime variant.</summary>
    DetectingGpu = 1,

    /// <summary>Determinate (bytes): streaming the release archive to a temp file.</summary>
    Downloading = 2,

    /// <summary>Indeterminate: recomputing the archive SHA256 to verify it against the pinned/published digest.</summary>
    Verifying = 3,

    /// <summary>Indeterminate: extracting the verified archive into the versioned cache directory.</summary>
    Extracting = 4,

    /// <summary>Terminal: the runtime is on disk and runnable.</summary>
    Completed = 5,

    /// <summary>
    ///     Terminal: the runtime could NOT be acquired. Never "some later provisioning step threw" — a model-download
    ///     failure has its own channel and must not be reported here.
    /// </summary>
    Failed = 6
}

/// <summary>
///     Sanitized runtime-acquisition status push payload, also served verbatim by the acquisition-status hydrate
///     endpoint so a late-joining client reconciles pushes and hydrate through one shape.
/// </summary>
/// <param name="Sequence">
///     Monotonic counter stamped by <see cref="IRuntimeAcquisitionStatusRegistry" /> on every status write, never reset
///     within a process lifetime. Hydrate and push travel different paths and race in BOTH directions, so the client
///     drops any update whose sequence is not greater than the one it already holds. Timestamps are not sufficient.
/// </param>
/// <param name="Phase">The <see cref="RuntimeAcquisitionPhase" /> name.</param>
/// <param name="Variant">The <see cref="GpuVariant" /> name being acquired, or <see langword="null" /> before it is known.</param>
/// <param name="Tag">The release tag being acquired, or <see langword="null" /> before it is resolved.</param>
/// <param name="CompletedBytes">Bytes written so far during <see cref="RuntimeAcquisitionPhase.Downloading" />; otherwise <see langword="null" />.</param>
/// <param name="TotalBytes">
///     The total download size when the response carried a <c>Content-Length</c>; <see langword="null" /> when unknown
///     (the pinned path has no catalog-reported size, so the total is simply absent until the headers land).
/// </param>
/// <param name="StepIndex">1-based index of the archive being acquired (the Windows-CUDA path fetches two).</param>
/// <param name="StepCount">How many archives this acquisition fetches in total (1, or 2 for Windows CUDA).</param>
/// <param name="SanitizedError">A user-safe reason when <see cref="Phase" /> is Failed; otherwise <see langword="null" />.</param>
public sealed record RuntimeAcquisitionStatusHubEvent(
    long Sequence,
    string Phase,
    string? Variant,
    string? Tag,
    long? CompletedBytes,
    long? TotalBytes,
    int StepIndex,
    int StepCount,
    string? SanitizedError);

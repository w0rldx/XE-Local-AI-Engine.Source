namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Coarse phase of an in-app CUDA <c>llama-server</c> build, surfaced to the UI for a progress label.
/// </summary>
public enum CudaBuildPhase
{
    /// <summary>No build has run (or the previous status was reset).</summary>
    Idle = 0,

    /// <summary>Cloning the pinned llama.cpp source at the pinned tag.</summary>
    Cloning = 1,

    /// <summary>Verifying the cloned tree's checked-out commit equals the pinned SHA.</summary>
    Verifying = 2,

    /// <summary>Configuring the CUDA build with cmake.</summary>
    Configuring = 3,

    /// <summary>Compiling the <c>llama-server</c> target.</summary>
    Building = 4,

    /// <summary>Validating + adopting the built binary as a managed runtime.</summary>
    Adopting = 5,

    /// <summary>The build finished successfully and the runtime was adopted.</summary>
    Completed = 6,

    /// <summary>The build was cancelled by the operator.</summary>
    Cancelled = 7,

    /// <summary>The build failed; <see cref="CudaBuildStatus.SanitizedError" /> carries a user-safe reason.</summary>
    Failed = 8
}

/// <summary>
///     A point-in-time snapshot of the in-app CUDA build: its <see cref="Phase" />, whether a build is currently
///     <see cref="IsRunning" />, whether the status is <see cref="Terminal" /> (Completed/Cancelled/Failed), the last N
///     streamed <see cref="LogLines" />, a sanitized error (on failure), and the pinned <see cref="Tag" /> being built.
/// </summary>
public sealed record CudaBuildStatus(
    CudaBuildPhase Phase,
    bool IsRunning,
    bool Terminal,
    IReadOnlyList<string> LogLines,
    string? SanitizedError,
    string? Tag,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc);

/// <summary>The outcome of a start request: whether a new build was started, or one was already in flight.</summary>
public enum CudaBuildStartOutcome
{
    /// <summary>A new build was started.</summary>
    Started = 0,

    /// <summary>A build is already running; the existing one continues (single-flight).</summary>
    AlreadyRunning = 1
}

/// <summary>
///     Orchestrates a single-flight, cancellable, background in-app CUDA <c>llama-server</c> build: clone (pinned URL+tag)
///     → verify the checked-out commit == the pinned SHA → detect+validate the compute arch → cmake configure → build →
///     atomically place under the cache root → validate + adopt as a managed runtime, streaming progress to the hub. Every
///     subprocess runs under a scrubbed, allowlisted environment in an owner-only work dir inside the cache root (never
///     <c>/tmp</c>). On any failure the partial tree is deleted and nothing is recorded.
/// </summary>
public interface ICudaBuildService
{
    /// <summary>
    ///     Starts a background build if none is in flight (single-flight). Performs a Linux + prerequisite + disk re-check
    ///     BEFORE spawning anything; a failed re-check throws a sanitized <see cref="LlamaRuntimeException" /> without
    ///     spawning a process. Returns immediately with <see cref="CudaBuildStartOutcome.Started" /> or
    ///     <see cref="CudaBuildStartOutcome.AlreadyRunning" />.
    /// </summary>
    Task<CudaBuildStartOutcome> StartAsync(CancellationToken ct);

    /// <summary>Returns the current build status snapshot (phase + last N log lines), safe to call any time.</summary>
    CudaBuildStatus GetStatus();

    /// <summary>
    ///     Requests cancellation of the in-flight build (idempotent). Returns <see langword="true" /> when a build was
    ///     running and was signalled, <see langword="false" /> when nothing was in flight.
    /// </summary>
    bool Cancel();

    /// <summary>
    ///     Startup recovery: deletes a stale build work directory left by a host crash/kill mid-build (detected by its
    ///     marker file). Best-effort; never throws. Called once at startup before any new build is allowed.
    /// </summary>
    void RecoverStaleWorkDirectory();
}

namespace XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;

/// <summary>Lifecycle state of the resident <c>whisper-server</c> daemon.</summary>
public enum WhisperRuntimeState
{
    /// <summary>No daemon is resident.</summary>
    Stopped = 0,

    /// <summary>A daemon is spawning or loading a model and is not yet answering.</summary>
    Starting = 1,

    /// <summary>A daemon is resident and has reported healthy.</summary>
    Ready = 2
}

/// <summary>Where the resolved <c>whisper-server</c> binary came from.</summary>
public enum WhisperBinarySource
{
    /// <summary>The pinned, hash-verified prebuilt release asset.</summary>
    Pinned = 0,

    /// <summary>A managed source build this node compiled and adopted.</summary>
    Managed = 1,

    /// <summary>An operator-supplied binary reached through the environment override.</summary>
    BringYourOwn = 2
}

/// <summary>The result of an eject: whether it happened, and the activity that blocked it when it did not.</summary>
public sealed record WhisperServerEvictResult(bool Evicted, WhisperRuntimeActivitySnapshot Activity);

/// <summary>A sanitized snapshot of the runtime for the operator UI. Carries no path, URL, or port.</summary>
public sealed record WhisperRuntimeStatusSnapshot(
    WhisperRuntimeState State,
    string? LoadedModelId,
    WhisperBackend? Backend,
    string? BinaryVersion,
    WhisperBinarySource? BinarySource,
    bool SupportsTranscode);

/// <summary>
///     A lease held for one in-flight transcription. Holding it keeps the idle reaper and any eject off the daemon for
///     the duration of the request; <see cref="Touch" /> refreshes the idle clock for a long one.
/// </summary>
public interface IWhisperTranscriptionLease : IDisposable
{
    void Touch();
}

/// <summary>
///     Owns the single resident <c>whisper-server</c> daemon: reuse-or-spawn it, readiness-gate it, switch its model in
///     place where that is cheaper than a respawn, tree-kill it on demand, and reap it when it goes idle.
/// </summary>
/// <remarks>
///     <para>
///         <b>One daemon, deliberately.</b> A node has one selected transcription model, and the server serializes
///         every request on a single mutex anyway, so a second daemon could never serve anyone faster. The
///         per-model dictionary, the loaded cap and the least-recently-used eviction that the image runtime needs are
///         therefore not ported.
///     </para>
///     <para>
///         <b>Why the lease is generation-bound.</b> <see cref="EnsureRunningAsync" /> returns an endpoint and the
///         caller acquires a lease as a second step. Between the two, another caller can complete a model switch on the
///         same mutable daemon, so the model id alone cannot say whether the instance that was resolved is still the
///         instance about to be used. The generation moves on every spawn and every successful switch, which makes the
///         pair unambiguous.
///     </para>
/// </remarks>
public interface IWhisperServerSupervisor
{
    /// <summary>
    ///     Ensures a ready daemon serving <paramref name="modelId" /> is running and returns its endpoint. Reuses a
    ///     live one, switches the model in place when the daemon is healthy, and otherwise spawns.
    /// </summary>
    /// <exception cref="WhisperRuntimeException">
    ///     The model is not installed, the runtime is busy with an exclusive operation, or the daemon failed to start
    ///     or become ready.
    /// </exception>
    Task<WhisperServerEndpoint> EnsureRunningAsync(string modelId, CancellationToken ct);

    /// <summary>
    ///     Tree-kills the resident daemon if one is running and nothing is in flight. Returns
    ///     <see cref="WhisperServerEvictResult.Evicted" /> <see langword="false" /> with the blocking activity when a
    ///     transcription or a spawn is active, which is what the endpoint turns into <c>409 runtime-busy</c>.
    /// </summary>
    Task<WhisperServerEvictResult> EvictAsync(CancellationToken ct);

    /// <summary>
    ///     Acquires a transcription lease against the resident daemon. Returns <see langword="null" /> when the daemon
    ///     no longer serves <paramref name="modelId" /> at <paramref name="generation" />, or is latched for eviction
    ///     or for a model switch.
    /// </summary>
    IWhisperTranscriptionLease? TryAcquireTranscriptionLease(string modelId, long generation);

    /// <summary>The current sanitized runtime status for the operator UI.</summary>
    WhisperRuntimeStatusSnapshot GetStatus();
}

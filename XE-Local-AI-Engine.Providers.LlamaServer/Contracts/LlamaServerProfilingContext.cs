namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     The per-spawn context handed to an operator profiling body by
///     <see cref="ILlamaServerProcessSupervisor.RunExclusiveProfilingAsync{T}" />.
/// </summary>
/// <remarks>
///     Its endpoint is valid only for the duration of the body: the supervisor evicts the transient, reaper-pinned
///     profiling process when the body returns or throws.
/// </remarks>
/// <param name="Endpoint">The localhost OpenAI-compatible endpoint of the exclusive profiling process.</param>
/// <param name="StartupOutput">Forwarded stdout and stderr lines captured up to the point the process became ready.</param>
/// <param name="FitParamsOutput">What <c>llama-fit-params</c> printed before launch; empty when it is missing or failed.</param>
/// <param name="ProcessId">
///     OS process id of the transient profiling server, for benchmark resource sampling only: ephemeral, never
///     persisted or exposed.
/// </param>
public sealed record LlamaServerProfilingContext(
    LlamaServerEndpoint Endpoint,
    IReadOnlyList<string> StartupOutput,
    IReadOnlyList<string> FitParamsOutput,
    int? ProcessId = null)
{
    /// <summary>
    ///     Ambient VRAM evidence captured after same-key eviction and immediately before this profiling process was
    ///     spawned. Benchmarks use it to reject pressure that already existed before model residency.
    /// </summary>
    public LlamaServerProfilingVramSnapshot? PreSpawnVram { get; init; }

    /// <summary>
    ///     Exact server argv for the candidate that reached readiness. For Explore profiling this distinguishes the
    ///     optimized KV/flash-attention plan from a successful safe fallback; failed-candidate arguments are never exposed.
    /// </summary>
    public IReadOnlyList<string> SuccessfulLaunchArguments { get; init; } = [];

    /// <summary>
    ///     Sanitized, content-free observation of the exact candidate that reached readiness. Report-only: it never
    ///     participates in admission.
    /// </summary>
    /// <remarks>
    ///     It carries runtime identity, spawn-through-readiness duration, measured placement class, primary or
    ///     safe-retry kind, and speculation class, for benchmark correlation.
    /// </remarks>
    public LlamaServerLoadObservation? LoadObservation { get; init; }

    /// <summary>
    ///     What this spawn actually launched, assembled after readiness. Populated for a BENCHMARK spawn only, and
    ///     <see langword="null" /> for every other profiling spawn or when the facts could not be assembled.
    /// </summary>
    /// <remarks>
    ///     A benchmark is the one spawn shape whose whole purpose is a measurement someone will later compare.
    /// </remarks>
    public LlamaServerLaunchReceipt? LaunchReceipt { get; init; }

    /// <summary>Creates a profiling context without machine-readable fit output (replay/benchmark callers).</summary>
    public LlamaServerProfilingContext(LlamaServerEndpoint endpoint, IReadOnlyList<string> startupOutput)
        : this(endpoint, startupOutput, FitParamsOutput: [], ProcessId: null)
    {
    }
}

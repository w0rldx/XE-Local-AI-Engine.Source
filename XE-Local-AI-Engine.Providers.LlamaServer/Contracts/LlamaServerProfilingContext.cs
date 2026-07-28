namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     The per-spawn context handed to an operator profiling body by
///     <see cref="ILlamaServerProcessSupervisor.RunExclusiveProfilingAsync{T}" />: the localhost endpoint of the
///     exclusively-spawned, reaper-pinned <c>llama-server</c> process plus a snapshot of the startup output captured
///     from both process pipes and the separate machine-readable stdout acquired from <c>llama-fit-params</c> before
///     launch. The endpoint is valid only for the duration of the body — the supervisor evicts the transient profiling
///     process when the body returns or throws.
/// </summary>
/// <param name="Endpoint">The localhost OpenAI-compatible endpoint of the exclusive profiling process.</param>
/// <param name="StartupOutput">
///     A snapshot of the forwarded stdout + stderr lines captured up to the point the process became ready.
/// </param>
/// <param name="FitParamsOutput">
///     The stdout lines emitted by the co-located <c>llama-fit-params</c> capability before the profiling server was
///     launched. Empty when the capability is missing or acquisition failed.
/// </param>
/// <param name="ProcessId">
///     OS process id of the transient profiling server. Supplied only for operator benchmark resource sampling; callers
///     must treat it as ephemeral and never persist or expose it.
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

    /// <summary>Creates a profiling context without machine-readable fit output (replay/benchmark callers).</summary>
    public LlamaServerProfilingContext(LlamaServerEndpoint endpoint, IReadOnlyList<string> startupOutput)
        : this(endpoint, startupOutput, FitParamsOutput: [], ProcessId: null)
    {
    }
}

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
public sealed record LlamaServerProfilingContext(
    LlamaServerEndpoint Endpoint,
    IReadOnlyList<string> StartupOutput,
    IReadOnlyList<string> FitParamsOutput)
{
    /// <summary>Creates a profiling context without machine-readable fit output (replay/benchmark callers).</summary>
    public LlamaServerProfilingContext(LlamaServerEndpoint endpoint, IReadOnlyList<string> startupOutput)
        : this(endpoint, startupOutput, FitParamsOutput: [])
    {
    }
}

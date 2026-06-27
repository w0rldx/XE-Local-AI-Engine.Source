namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     The per-spawn context handed to an operator profiling body by
///     <see cref="ILlamaServerProcessSupervisor.RunExclusiveProfilingAsync{T}" />: the localhost endpoint of the
///     exclusively-spawned, reaper-pinned <c>llama-server</c> process plus a snapshot of the startup output captured
///     from BOTH the stdout and stderr pipes during launch (the fit/device banners the benchmark/explore harness
///     parses). The endpoint is valid only for the duration of the body — the supervisor evicts the transient
///     profiling process when the body returns or throws.
/// </summary>
/// <param name="Endpoint">The localhost OpenAI-compatible endpoint of the exclusive profiling process.</param>
/// <param name="StartupOutput">
///     A snapshot of the forwarded stdout + stderr lines captured up to the point the process became ready.
/// </param>
public sealed record LlamaServerProfilingContext(LlamaServerEndpoint Endpoint, IReadOnlyList<string> StartupOutput);

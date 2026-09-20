namespace XE_Local_AI_Engine.Client.Services.Capacity;

using XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>
///     The single admission gate before any local sub-agent model loads: provider class (cloud, llama.cpp or Ollama), the running-models
///     snapshot, the footprint estimate, the byte budget and the process cap fold into one <see cref="CapacityDecision" /> per model+role.
/// </summary>
/// <remarks>
///     A cloud model bypasses the byte and process probe entirely (no local cost); a local model already running for the same
///     <c>(model, role)</c> queues on that process rather than loading a second one. Otherwise it is admitted only when it fits the free
///     byte budget (VRAM, or RAM) AND leaves process-count headroom, and an admitted local model reserves its footprint in the
///     pending-footprint ledger and publishes its exact llama.cpp launch identity — both released by the caller on child exit via
///     <see cref="CapacityDecision.Reservation" />. An unknown footprint or budget with no RAM fallback rejects: conservative on uncertainty.
/// </remarks>
public interface ICapacityService
{
    /// <summary>
    ///     Decides whether a sub-agent bound to <paramref name="modelName" /> in <paramref name="role" /> may be
    ///     spawned. The local read-decide-reserve runs under the process-wide ledger gate so concurrent different-model
    ///     spawns cannot both pass on the same snapshot. Flows <paramref name="ct" /> to every probe.
    /// </summary>
    Task<CapacityDecision> DecideAsync(string modelName, ModelRole role, CancellationToken ct);

    /// <summary>
    ///     Decides capacity for a caller that must launch with a specific context window. Existing callers use the
    ///     model/role overload and retain the current automatic-context behavior.
    /// </summary>
    Task<CapacityDecision> DecideAsync(CapacityRequest request, CancellationToken ct) =>
        DecideAsync(request.ModelName, request.Role, ct);
}

/// <summary>Context-aware capacity request used by frozen benchmark execution.</summary>
public sealed record CapacityRequest
{
    public required string ModelName { get; init; }

    public required ModelRole Role { get; init; }

    public int? RequiredContextTokens { get; init; }

    /// <summary>Whether an llama.cpp Allow also publishes a launch admission the next spawn is expected to CONSUME.</summary>
    /// <remarks>
    ///     A caller that launches its own process from frozen arguments (a benchmark replay) must say <see langword="false" />: the admission
    ///     it never consumes is exactly what the supervisor refuses to launch against. The ledger reservation is unaffected either way, so
    ///     the bytes are still booked.
    /// </remarks>
    public bool PublishLaunchAdmission { get; init; } = true;

    /// <summary>
    ///     The llama.cpp KV-cache element type (<c>q8_0</c>/<c>q4_0</c>) the caller will ACTUALLY launch with, so the ledger reservation
    ///     sizes its KV term against that instead of the conservative fp16 default.
    /// </summary>
    /// <remarks>
    ///     <see langword="null" /> — every caller but a benchmark run — keeps the fp16 sizing, and so does an f16 or unrecognized value. It
    ///     can only ever reserve LESS: the context window is still chosen against the fp16 estimate, and only the returned allocation's
    ///     bytes are re-sized.
    /// </remarks>
    public string? KvCacheType { get; init; }
}

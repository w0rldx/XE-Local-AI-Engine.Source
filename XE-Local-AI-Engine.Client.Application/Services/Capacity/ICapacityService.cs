namespace XE_Local_AI_Engine.Client.Services.Capacity;

using XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>
///     The single admission gate before any local sub-agent model loads. Composes the provider class (cloud vs local
///     vs Ollama), the running-models snapshot, the model's footprint estimate, the byte budget (VRAM or RAM), and the
///     existing process-count cap into one <see cref="CapacityDecision" /> keyed on the sub-agent's <c>(model, role)</c>.
/// </summary>
/// <remarks>
///     Cloud models bypass the byte/process probe entirely (no local cost). A local model already running for the same
///     <c>(model, role)</c> queues on that process (no second load). Otherwise the model is admitted only when it fits
///     the free byte budget AND leaves process-count headroom; an admitted local model reserves its footprint in the
///     pending-footprint ledger and publishes its exact llama.cpp launch identity (both released by the caller on child
///     exit via <see cref="CapacityDecision.Reservation" />).
///     Unknown footprint or unknown budget with no RAM fallback rejects (conservative on uncertainty).
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
public sealed record CapacityRequest(string ModelName, ModelRole Role, int? RequiredContextTokens = null);

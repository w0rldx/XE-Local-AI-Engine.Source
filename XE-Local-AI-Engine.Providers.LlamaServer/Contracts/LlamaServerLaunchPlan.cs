namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     The resolved launch-policy decision for one normal (resolver-driven) spawn — the context window, GPU KV-cache
///     quantization/flash-attention choice, and CPU thread counts the supervisor's launch-spec builder emits ON TOP of a
///     spawn's <see cref="ResolvedLaunchArguments" />. Produced by <see cref="ILlamaServerLaunchPolicy" />.
/// </summary>
/// <remarks>
///     A frozen-profile replay owns its own <c>-c</c>/KV/FA verbatim, so for a replay <see cref="RequestedContextTokens" />
///     is <see langword="null" /> and <see cref="UseKvCacheQuantization" /> is <see langword="false" /> — the plan then
///     carries only the (variant-appropriate) CPU thread counts. Replay profiling bypasses the policy and is built with no
///     plan; explore profiling deliberately uses the production plan so helper/server placement evidence is equivalent to
///     normal serving.
/// </remarks>
/// <param name="RequestedContextTokens">
///     The <c>-c</c> value to emit (already capped to the model's train context), or <see langword="null" /> to leave the
///     context to the spawn's own args (a frozen replay pins its own <c>-c</c>).
/// </param>
/// <param name="UseKvCacheQuantization">
///     When <see langword="true" /> (GPU build, policy enabled, no recorded fallback), emit <c>-fa on</c> plus
///     <c>-ctk/-ctv <see cref="KvCacheType" /></c>.
/// </param>
/// <param name="KvCacheType">The KV-cache element type emitted for both <c>-ctk</c> and <c>-ctv</c> (e.g. <c>q8_0</c>).</param>
/// <param name="CpuThreads">Generation thread count (<c>-t</c>), or <see langword="null" /> to leave it unset (GPU build).</param>
/// <param name="CpuThreadsBatch">Prompt-batch thread count (<c>-tb</c>), or <see langword="null" /> to leave it unset (GPU build).</param>
/// <param name="CpuMoe">
///     Emit <c>--cpu-moe</c>, keeping every Mixture-of-Experts weight in system RAM. Set only on a GPU explore spawn
///     whose admitted allocation placed the experts there. <strong>The flag IS the placement</strong>: the admission
///     ledger booked this process's footprint on the premise that the expert share is NOT in VRAM, so a launch that
///     drops the flag runs outside what was reserved for it. That is why the capability gate refuses a runtime without
///     it rather than degrading, and why the safe retry carries it through untouched.
/// </param>
/// <param name="CpuMoeLayers">
///     Reserved for a future partial offload (<c>--n-cpu-moe N</c>). Always <see langword="null" /> today: the estimator
///     offloads the WHOLE expert share, so the derived N is always every layer, which is <c>--cpu-moe</c>.
/// </param>
public readonly record struct LlamaServerLaunchPlan(
    int? RequestedContextTokens,
    bool UseKvCacheQuantization,
    string KvCacheType,
    int? CpuThreads,
    int? CpuThreadsBatch,
    bool CpuMoe = false,
    int? CpuMoeLayers = null)
{
    /// <summary>The safe (KV-quant/flash-attention off) variant of this plan used for the one-shot fallback retry.</summary>
    public LlamaServerLaunchPlan WithoutKvCacheQuantization()
    {
        return this with
        {
            UseKvCacheQuantization = false
        };
    }
}

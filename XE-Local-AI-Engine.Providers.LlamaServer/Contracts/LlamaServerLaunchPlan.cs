namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     The resolved launch-policy decision for one normal (resolver-driven) spawn — the context window, GPU KV-cache
///     quantization/flash-attention choice, and CPU thread counts the supervisor's launch-spec builder emits ON TOP of a
///     spawn's <see cref="ResolvedLaunchArguments" />. Produced by <see cref="ILlamaServerLaunchPolicy" />.
/// </summary>
/// <remarks>
///     A frozen-profile replay owns its own <c>-c</c>/KV/FA verbatim, so for a replay <see cref="RequestedContextTokens" />
///     is <see langword="null" /> and <see cref="UseKvCacheQuantization" /> is <see langword="false" /> — the plan then
///     carries only the (variant-appropriate) CPU thread counts. Operator profiling spawns bypass the policy entirely and
///     are built with no plan.
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
public readonly record struct LlamaServerLaunchPlan(
    int? RequestedContextTokens,
    bool UseKvCacheQuantization,
    string KvCacheType,
    int? CpuThreads,
    int? CpuThreadsBatch)
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

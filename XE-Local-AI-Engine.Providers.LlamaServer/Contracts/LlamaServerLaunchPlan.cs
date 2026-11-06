namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>The resolved launch-policy decision for one normal, resolver-driven spawn, produced by <see cref="ILlamaServerLaunchPolicy" />.</summary>
/// <remarks>
///     What the launch-spec builder emits ON TOP of a spawn's <see cref="ResolvedLaunchArguments" />: window, GPU KV-quant and
///     flash attention, CPU threads, chat slots. A frozen-profile replay owns its <c>-c</c>/KV/FA verbatim; replay profiling
///     bypasses the policy with no plan, while explore profiling uses the production plan so its placement evidence matches serving.
/// </remarks>
/// <param name="RequestedContextTokens">The <c>-c</c> to emit, already capped to the train context, or <see langword="null" /> to leave it to the spawn's own args.</param>
/// <param name="UseKvCacheQuantization">When true — GPU build, policy enabled, no recorded fallback — emit <c>-fa on</c> plus <c>-ctk</c>/<c>-ctv</c>.</param>
/// <param name="KvCacheType">The KV-cache element type emitted for both <c>-ctk</c> and <c>-ctv</c> (for example <c>q8_0</c>).</param>
/// <param name="CpuThreads">Generation thread count (<c>-t</c>), or <see langword="null" /> to leave it unset (GPU build).</param>
/// <param name="CpuThreadsBatch">Prompt-batch thread count (<c>-tb</c>), or <see langword="null" /> to leave it unset (GPU build).</param>
/// <param name="CpuMoe">Emit <c>--cpu-moe</c>, keeping every expert weight in system RAM; the flag IS the admitted placement, so it is never dropped.</param>
/// <param name="CpuMoeLayers">Reserved for a future partial offload (<c>--n-cpu-moe N</c>); always <see langword="null" />, the estimator offloading the WHOLE expert share.</param>
/// <param name="ChatSlots">The admitted chat slot count (<see cref="ProcessContextAllocation.ChatSlots" />); ignored for other roles.</param>
public readonly record struct LlamaServerLaunchPlan(
    int? RequestedContextTokens,
    bool UseKvCacheQuantization,
    string KvCacheType,
    int? CpuThreads,
    int? CpuThreadsBatch,
    bool CpuMoe = false,
    int? CpuMoeLayers = null,
    int ChatSlots = LlamaServerChatSlots.Count)
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

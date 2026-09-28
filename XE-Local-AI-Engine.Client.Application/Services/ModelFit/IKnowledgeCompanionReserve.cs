namespace XE_Local_AI_Engine.Client.Services.ModelFit;

using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;

/// <summary>
///     The VRAM the knowledge companions (configured reranker, llama.cpp embedder) will claim once warm, which chat model
///     recommendations must leave free.
/// </summary>
/// <remarks>
///     A resident companion is skipped only when the budget is measured free VRAM, which already excludes it; against total
///     VRAM every companion counts. An unknown footprint counts as zero, so the reserve shrinks a budget only by what is estimable.
/// </remarks>
public interface IKnowledgeCompanionReserve
{
    /// <summary>The summed GPU bytes of the configured companions the budget in <paramref name="profile" /> does not already net out; zero when none applies.</summary>
    Task<long> ResolveGpuBytesAsync(HardwareProfile profile, CancellationToken ct);
}

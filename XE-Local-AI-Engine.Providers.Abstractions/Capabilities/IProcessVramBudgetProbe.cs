namespace XE_Local_AI_Engine.Providers.Abstractions.Capabilities;

/// <summary>
///     Probes llama.cpp's CURRENT PROCESS VRAM BUDGET for a backend. The real implementation parses
///     <c>llama-server --list-devices</c>, which is vendor-agnostic and placement-relevant, but on WDDM/Windows it is not
///     system-wide free VRAM: CUDA reports the calling process's residency budget and can ignore memory held by games or
///     other model processes. Consumers that need global admission/invalidation semantics must prefer
///     <see cref="HardwareProfile.AvailableVramBytes" /> and treat this value as a separate fallback/evidence axis.
/// </summary>
/// <remarks>
///     <para>
///         <paramref name="backend" /> is the lowercase backend token an inference profile persists
///         (<c>cuda</c> / <c>vulkan</c> / <c>cpu</c>). A plain string is taken deliberately: the acceleration-variant
///         type (<c>GpuVariant</c>) lives in <c>Providers.LlamaServer</c>, and <c>Providers.Abstractions</c> must NOT
///         depend on it — the string keeps this seam dependency-clean while still conveying the variant the real probe
///         (the <c>--list-devices</c> parser) needs.
///     </para>
/// </remarks>
public interface IProcessVramBudgetProbe
{
    /// <summary>
    ///     Returns llama.cpp's process-local VRAM budget in bytes for <paramref name="backend" />, or
    ///     <see langword="null" /> when the figure is unknown/unsupported.
    /// </summary>
    Task<long?> TryGetProcessBudgetBytesAsync(string backend, CancellationToken ct);
}

/// <summary>
///     Default <see cref="IProcessVramBudgetProbe" /> that always reports "unknown" (<see langword="null" />). Wired via
///     <c>TryAddSingleton</c> so callers degrade when the real <c>--list-devices</c>-backed probe is unavailable.
/// </summary>
public sealed class UnknownProcessVramBudgetProbe : IProcessVramBudgetProbe
{
    /// <inheritdoc />
    public Task<long?> TryGetProcessBudgetBytesAsync(string backend, CancellationToken ct)
    {
        return Task.FromResult<long?>(null);
    }
}

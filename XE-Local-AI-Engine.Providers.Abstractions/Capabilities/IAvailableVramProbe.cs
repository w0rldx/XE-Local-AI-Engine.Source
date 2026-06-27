namespace XE_Local_AI_Engine.Providers.Abstractions.Capabilities;

/// <summary>
///     Probes the host for the CURRENTLY-FREE GPU VRAM available to a llama.cpp backend. Consumed by inference-profile
///     invalidation to detect a frozen profile whose freeze-time free-VRAM baseline no longer holds. Returns
///     <see langword="null" /> whenever the figure is unknown or unsupported (no GPU, CPU backend, or no real probe
///     wired) so callers DEGRADE rather than treating "unknown" as "zero".
/// </summary>
/// <remarks>
///     <para>
///         <paramref name="backend" /> is the lowercase backend token an inference profile persists
///         (<c>cuda</c> / <c>vulkan</c> / <c>cpu</c>). A plain string is taken deliberately: the acceleration-variant
///         type (<c>GpuVariant</c>) lives in <c>Providers.LlamaServer</c>, and <c>Providers.Abstractions</c> must NOT
///         depend on it — the string keeps this seam dependency-clean while still conveying the variant the real probe
///         (Lane B's <c>--list-devices</c> parser) needs.
///     </para>
/// </remarks>
public interface IAvailableVramProbe
{
    /// <summary>
    ///     Returns the currently-free VRAM in bytes for <paramref name="backend" />, or <see langword="null" /> when the
    ///     figure is unknown/unsupported.
    /// </summary>
    Task<long?> TryGetFreeVramBytesAsync(string backend, CancellationToken ct);
}

/// <summary>
///     Default <see cref="IAvailableVramProbe" /> that always reports "unknown" (<see langword="null" />). Wired via
///     <c>TryAddSingleton</c> so the invalidation evaluator simply skips the live free-VRAM check until Lane B replaces
///     it with the real <c>--list-devices</c>-backed probe.
/// </summary>
public sealed class UnknownAvailableVramProbe : IAvailableVramProbe
{
    /// <inheritdoc />
    public Task<long?> TryGetFreeVramBytesAsync(string backend, CancellationToken ct)
    {
        return Task.FromResult<long?>(null);
    }
}

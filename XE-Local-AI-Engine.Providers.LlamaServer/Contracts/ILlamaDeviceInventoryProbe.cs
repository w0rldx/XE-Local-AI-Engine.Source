namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Probes the devices a llama.cpp binary actually enumerates for an acceleration variant, by running the SELECTED
///     binary's device listing and parsing its table.
/// </summary>
/// <remarks>
///     Consumed by the runtime device audit to detect a silent CPU fallback: a GPU-variant binary that enumerates zero
///     devices — the shipped Vulkan build under WSL2 with no Vulkan ICD — still runs, but on the CPU. The answer is a
///     pure function of the resolved binary, so implementations cache it per (variant, binary path, binary mtime); a
///     CPU variant short-circuits to an empty list without spawning, and every failure mode degrades to
///     <see cref="LlamaDeviceInventory.Unknown" /> rather than throwing, so a glitch never raises a false alarm.
/// </remarks>
public interface ILlamaDeviceInventoryProbe
{
    /// <summary>
    ///     Returns the devices the <paramref name="variant" /> binary enumerates. Genuine caller cancellation is honored;
    ///     every other failure degrades to <see cref="LlamaDeviceInventory.Unknown" />.
    /// </summary>
    Task<LlamaDeviceInventory> GetDeviceInventoryAsync(GpuVariant variant, CancellationToken ct);
}

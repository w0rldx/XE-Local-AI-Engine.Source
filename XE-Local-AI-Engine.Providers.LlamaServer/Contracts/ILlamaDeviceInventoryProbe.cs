namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Probes the devices a llama.cpp binary actually enumerates for an acceleration variant, by running the SELECTED
///     binary's <c>llama-server --list-devices</c> and parsing its device table. Consumed by the runtime device audit
///     to detect a silent CPU fallback — a GPU-variant binary that enumerates zero devices (e.g. the shipped
///     Vulkan build under WSL2 with no Vulkan ICD) still runs, but on the CPU.
/// </summary>
/// <remarks>
///     The answer is a pure function of the resolved binary, so implementations cache it per (variant, binary path,
///     binary mtime) — it only changes when the binary changes. A CPU variant short-circuits to a determinate empty list
///     without spawning a process. Every failure mode (spawn failure, timeout) degrades to
///     <see cref="LlamaDeviceInventory.Unknown" /> rather than throwing, so a probe glitch never raises a false alarm.
/// </remarks>
public interface ILlamaDeviceInventoryProbe
{
    /// <summary>
    ///     Returns the devices the <paramref name="variant" /> binary enumerates. Genuine caller cancellation is honored;
    ///     every other failure degrades to <see cref="LlamaDeviceInventory.Unknown" />.
    /// </summary>
    Task<LlamaDeviceInventory> GetDeviceInventoryAsync(GpuVariant variant, CancellationToken ct);
}

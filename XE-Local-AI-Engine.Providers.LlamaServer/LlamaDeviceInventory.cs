namespace XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>
///     A single GPU device as reported by <c>llama-server --list-devices</c>: its name and, where the build prints them,
///     its total / currently-free VRAM in bytes (either may be <see langword="null" /> when a build omits the column).
/// </summary>
public sealed record LlamaGpuDevice(string Name, long? TotalBytes, long? FreeBytes);

/// <summary>
///     The devices a specific llama.cpp binary actually enumerates for a given acceleration variant — the structured
///     answer to "did the SELECTED runtime find the GPU?", used by the post-spawn device audit. Distinguishes
///     a probe that RAN and saw a (possibly empty) device list from a probe that could not run (timeout / failure):
///     <see cref="ProbeSucceeded" /> gates whether <see cref="Devices" /> is authoritative, so a failed probe never
///     raises a false CPU-fallback alarm.
/// </summary>
public sealed record LlamaDeviceInventory
{
    /// <summary>The acceleration variant whose binary was probed.</summary>
    public required GpuVariant Variant { get; init; }

    /// <summary>
    ///     <see langword="true" /> when the <c>--list-devices</c> probe ran to a determinate answer (including an
    ///     authoritative empty device list); <see langword="false" /> when it could not run (spawn failure / timeout),
    ///     in which case <see cref="Devices" /> is empty but MUST be treated as "unknown", never as "no GPU".
    /// </summary>
    public required bool ProbeSucceeded { get; init; }

    /// <summary>The enumerated GPU devices (empty when none, or when <see cref="ProbeSucceeded" /> is false).</summary>
    public required IReadOnlyList<LlamaGpuDevice> Devices { get; init; }

    /// <summary><see langword="true" /> when the probe ran and enumerated at least one GPU device.</summary>
    public bool HasGpuDevice => ProbeSucceeded && Devices.Count > 0;

    /// <summary>A determinate empty device list (the probe ran and saw no GPU — e.g. a CPU build, or Vulkan with no ICD).</summary>
    public static LlamaDeviceInventory Empty(GpuVariant variant)
    {
        return new LlamaDeviceInventory
        {
            Variant = variant,
            ProbeSucceeded = true,
            Devices = []
        };
    }

    /// <summary>An indeterminate result (the probe could not run) — never treated as "no GPU".</summary>
    public static LlamaDeviceInventory Unknown(GpuVariant variant)
    {
        return new LlamaDeviceInventory
        {
            Variant = variant,
            ProbeSucceeded = false,
            Devices = []
        };
    }
}

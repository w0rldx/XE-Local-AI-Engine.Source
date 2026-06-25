namespace XE_Local_AI_Engine.Providers.Capabilities.Contracts;

/// <summary>
///     OS/filesystem facts the <see cref="HardwareProfiler" /> needs but that cannot be faked through
///     <see cref="IProcessProbe" /> alone (the platform switch, raw <c>/proc</c> and <c>/sys</c> reads, RAM/disk
///     queries). Split out so every OS branch is unit-testable with canned values and no real hardware.
/// </summary>
internal interface IHardwareProbeEnvironment
{
    /// <summary><see langword="true" /> on Windows (<see cref="System.OperatingSystem.IsWindows" />).</summary>
    bool IsWindows { get; }

    /// <summary><see langword="true" /> on Linux (<see cref="System.OperatingSystem.IsLinux" />).</summary>
    bool IsLinux { get; }

    /// <summary>Logical CPU core count.</summary>
    int ProcessorCount { get; }

    /// <summary>Raw contents of <c>/proc/meminfo</c> (Linux), or <see langword="null" /> when unavailable.</summary>
    string? ReadProcMemInfo();

    /// <summary>
    ///     The 4-digit hex PCI vendor ids reported by <c>/sys/class/drm/*/device/vendor</c> (Linux), upper-cased without
    ///     the <c>0x</c> prefix (matching the <c>10DE</c>/<c>1002</c>/<c>8086</c> vendor-id constants). Empty when none / unavailable.
    /// </summary>
    IReadOnlyList<string> ReadDrmVendorIds();

    /// <summary>Total physical RAM in bytes as reported by the OS (used on Windows; Linux prefers meminfo).</summary>
    long GetTotalPhysicalMemoryBytes();

    /// <summary>Available (allocatable) RAM in bytes as reported by the OS (Windows fallback when meminfo is absent).</summary>
    long GetAvailableMemoryBytes();

    /// <summary>Free disk bytes on the volume hosting <paramref name="path" />; <c>0</c> when it cannot be resolved.</summary>
    long GetFreeDiskBytes(string path);
}

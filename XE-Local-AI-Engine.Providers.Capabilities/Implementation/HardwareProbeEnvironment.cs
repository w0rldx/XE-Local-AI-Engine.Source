namespace XE_Local_AI_Engine.Providers.Capabilities.Implementation;

using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Capabilities.Contracts;

/// <summary>
///     Live <see cref="IHardwareProbeEnvironment" /> over the real OS. Every read degrades to a safe default
///     (<see langword="null" />/empty/0) on failure so the profiler can fall through to its CPU-mode floor.
/// </summary>
internal sealed class HardwareProbeEnvironment : IHardwareProbeEnvironment
{
    private const string ProcMemInfoPath = "/proc/meminfo";
    private const string DrmClassPath = "/sys/class/drm";

    // The one free-disk measurement in the node; see DriveInfoFreeSpaceProbe for why a path root is the wrong input.
    private static readonly IFreeSpaceProbe FreeSpace = new DriveInfoFreeSpaceProbe();

    /// <inheritdoc />
    public bool IsWindows => OperatingSystem.IsWindows();

    /// <inheritdoc />
    public bool IsLinux => OperatingSystem.IsLinux();

    /// <inheritdoc />
    public int ProcessorCount => Environment.ProcessorCount;

    /// <inheritdoc />
    public string? ReadProcMemInfo()
    {
        try
        {
            return File.Exists(ProcMemInfoPath) ? File.ReadAllText(ProcMemInfoPath) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> ReadDrmVendorIds()
    {
        try
        {
            if (!Directory.Exists(DrmClassPath))
            {
                return [];
            }

            var vendorIds = new List<string>();
            foreach (var cardPath in Directory.EnumerateDirectories(DrmClassPath))
            {
                var vendorFile = Path.Combine(cardPath, "device", "vendor");
                if (!File.Exists(vendorFile))
                {
                    continue;
                }

                var raw = File.ReadAllText(vendorFile).Trim();
                if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    raw = raw[2..];
                }

                if (raw.Length > 0)
                {
                    // Normalize to a stable, culture-invariant case for comparison against the PCI vendor-id constants.
                    vendorIds.Add(raw.ToUpperInvariant());
                }
            }

            return vendorIds;
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <inheritdoc />
    public long GetTotalPhysicalMemoryBytes()
    {
        return GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
    }

    /// <inheritdoc />
    public long GetAvailableMemoryBytes()
    {
        var memoryInfo = GC.GetGCMemoryInfo();
        var available = memoryInfo.TotalAvailableMemoryBytes - memoryInfo.MemoryLoadBytes;
        return available > 0 ? available : 0;
    }

    /// <inheritdoc />
    public long GetFreeDiskBytes(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            return FreeSpace.GetAvailableFreeBytes(path);
        }
        // An unmeasurable path is this probe's 0, never a throw: the profiler falls through to its CPU-mode floor.
        // InvalidOperationException is the shared probe's "nothing exists at or above the path"; a drive that is not
        // ready arrives as an IOException out of DriveInfo, which is the same 0 the old IsReady gate answered.
        catch (Exception exception)
            when (exception is IOException or ArgumentException or UnauthorizedAccessException or InvalidOperationException)
        {
            return 0;
        }
    }
}

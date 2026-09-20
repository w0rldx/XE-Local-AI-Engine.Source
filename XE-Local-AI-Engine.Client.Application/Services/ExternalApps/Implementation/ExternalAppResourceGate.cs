namespace XE_Local_AI_Engine.Client.Services.ExternalApps.Implementation;

using System.Globalization;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     The admission gate: has this machine the memory and the disk to take one more application.
/// </summary>
/// <remarks>
///     This is the ONLY consumer of the manifest's memory figures. They are not per-container ceilings — V1 sets
///     none — so a figure used here and nowhere else is the whole of their meaning. There is no reservation ledger
///     either: two installs started in the same second can both pass, which is a deliberate simplification for a
///     single-user desktop node rather than an oversight.
/// </remarks>
internal sealed class ExternalAppResourceGate
{
    /// <summary>Headroom above the manifest's own minimum, so an install does not leave the box with nothing to spare.</summary>
    internal const long MemoryHeadroomBytes = 512L * 1024 * 1024;

    /// <summary>Free disk an install requires, independent of image size: the pull figure is not knowable in advance.</summary>
    internal const long RequiredDiskBytes = 2L * 1024 * 1024 * 1024;

    private readonly IRuntimeDeviceAudit _audit;
    private readonly INodeDataDirectory _dataDirectory;
    private readonly IFreeSpaceProbe _freeSpace;

    public ExternalAppResourceGate(IRuntimeDeviceAudit audit, INodeDataDirectory dataDirectory, IFreeSpaceProbe freeSpace)
    {
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(dataDirectory);
        ArgumentNullException.ThrowIfNull(freeSpace);
        _audit = audit;
        _dataDirectory = dataDirectory;
        _freeSpace = freeSpace;
    }

    public async Task<ExternalAppResourceVerdict> EvaluateAsync(ApplicationManifest manifest, string? instanceRoot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var profile = await _audit.GetEffectiveProfileAsync(forceRefreshProfile: true, ct);

        var requiredMemory = ((long)manifest.Resources.MinimumMemoryMb * 1024 * 1024) + MemoryHeadroomBytes;
        var availableMemory = profile.AvailableRamBytes;
        var availableDisk = MeasureFreeDisk(string.IsNullOrWhiteSpace(instanceRoot) ? _dataDirectory.Root : instanceRoot, profile.FreeDiskBytes);

        if (availableMemory < requiredMemory)
        {
            return new ExternalAppResourceVerdict
            {
                Satisfied = false,
                FailureCategory = ExternalAppFailureCategory.InsufficientMemory,
                RequiredMemoryBytes = requiredMemory,
                AvailableMemoryBytes = availableMemory,
                RequiredDiskBytes = RequiredDiskBytes,
                AvailableDiskBytes = availableDisk,
                Message = Describe("memory", requiredMemory, availableMemory)
            };
        }

        if (availableDisk < RequiredDiskBytes)
        {
            return new ExternalAppResourceVerdict
            {
                Satisfied = false,
                FailureCategory = ExternalAppFailureCategory.InsufficientDisk,
                RequiredMemoryBytes = requiredMemory,
                AvailableMemoryBytes = availableMemory,
                RequiredDiskBytes = RequiredDiskBytes,
                AvailableDiskBytes = availableDisk,
                Message = Describe("disk space", RequiredDiskBytes, availableDisk)
            };
        }

        return new ExternalAppResourceVerdict
        {
            Satisfied = true,
            FailureCategory = null,
            RequiredMemoryBytes = requiredMemory,
            AvailableMemoryBytes = availableMemory,
            RequiredDiskBytes = RequiredDiskBytes,
            AvailableDiskBytes = availableDisk,
            Message = "This machine has the memory and disk this application asks for."
        };
    }

    /// <summary>Free space where the instance directory will live, because the hardware profile measures the models volume and the two are routinely different disks.</summary>
    /// <remarks>
    ///     The probe resolves the filesystem: the instance directory does not exist yet on the admission path, so it measures the closest
    ///     existing ancestor. A zero falls back as hard as an exception does — on a UNC path or inside a container bind the measurement
    ///     returns nonsense rather than throwing, and "could not measure" must never be shown to a user as "your disk is full".
    /// </remarks>
    private long MeasureFreeDisk(string instanceRoot, long profileFallback)
    {
        try
        {
            var available = _freeSpace.GetAvailableFreeBytes(instanceRoot);
            if (available > 0)
            {
                return available;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            // Fall through to the profile figure below.
        }

        return profileFallback;
    }

    private static string Describe(string resource, long required, long available)
    {
        return string.Create(CultureInfo.InvariantCulture,
            $"This application needs about {Gibibytes(required)} GiB of {resource}; {Gibibytes(available)} GiB is available.");
    }

    private static string Gibibytes(long bytes)
    {
        return (bytes / (double)(1024 * 1024 * 1024)).ToString("0.0", CultureInfo.InvariantCulture);
    }
}

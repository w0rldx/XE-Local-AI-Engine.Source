namespace XE_Local_AI_Engine.Tests.ExternalApps;

using NSubstitute;
using NSubstitute.ExceptionExtensions;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.ExternalApps;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.HuggingFace.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The admission gate. The manifest's memory figures are read here and nowhere else, so these are the tests
///     that say what those figures mean.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class ExternalAppResourceGateTests
{
    private const long Gibibyte = 1024L * 1024 * 1024;

    [Test]
    public async Task Evaluate_WithRoomToSpare_Passes()
    {
        var verdict = await Evaluate(minimumMemoryMb: 4096, availableRamBytes: 16 * Gibibyte, freeDiskBytes: 100 * Gibibyte);

        AssertEx.True(verdict.Satisfied, verdict.Message);
        AssertEx.Null(verdict.FailureCategory);
        AssertEx.Equal((4096L * 1024 * 1024) + ExternalAppResourceGate.MemoryHeadroomBytes, verdict.RequiredMemoryBytes);
        AssertEx.Equal(ExternalAppResourceGate.RequiredDiskBytes, verdict.RequiredDiskBytes);
    }

    /// <summary>
    ///     One byte short is short. The headroom above the manifest's minimum is part of the requirement, not a
    ///     courtesy, so the boundary is asserted rather than described.
    /// </summary>
    [Test]
    public async Task Evaluate_WithMemoryShortByOneByte_ReportsInsufficientMemory()
    {
        var required = (4096L * 1024 * 1024) + ExternalAppResourceGate.MemoryHeadroomBytes;

        var verdict = await Evaluate(minimumMemoryMb: 4096, availableRamBytes: required - 1, freeDiskBytes: 100 * Gibibyte);

        AssertEx.False(verdict.Satisfied, "One byte short of the requirement is not enough.");
        AssertEx.Equal(ExternalAppFailureCategory.InsufficientMemory, verdict.FailureCategory);
        AssertEx.Equal(required - 1, verdict.AvailableMemoryBytes);
    }

    [Test]
    public async Task Evaluate_WithExactlyEnoughMemory_Passes()
    {
        var required = (4096L * 1024 * 1024) + ExternalAppResourceGate.MemoryHeadroomBytes;

        var verdict = await Evaluate(minimumMemoryMb: 4096, availableRamBytes: required, freeDiskBytes: 100 * Gibibyte);

        AssertEx.True(verdict.Satisfied, verdict.Message);
    }

    [Test]
    public async Task Evaluate_WithTooLittleDisk_ReportsInsufficientDisk()
    {
        var verdict = await Evaluate(minimumMemoryMb: 512,
            availableRamBytes: 16 * Gibibyte,
            freeDiskBytes: 1,
            instanceRoot: MissingVolumePath());

        AssertEx.False(verdict.Satisfied, "One byte of free disk is not enough for an application.");
        AssertEx.Equal(ExternalAppFailureCategory.InsufficientDisk, verdict.FailureCategory);
        AssertEx.Equal(expected: 1L, verdict.AvailableDiskBytes);
    }

    /// <summary>
    ///     A path whose volume cannot be measured falls back to the hardware profile's figure. On a UNC path or
    ///     inside a container bind <c>DriveInfo</c> returns nonsense rather than throwing, and "could not measure"
    ///     must never be shown as "your disk is full".
    /// </summary>
    [Test]
    public async Task Evaluate_WhenTheVolumeCannotBeMeasured_FallsBackToTheProfile()
    {
        var verdict = await Evaluate(minimumMemoryMb: 512,
            availableRamBytes: 16 * Gibibyte,
            freeDiskBytes: 100 * Gibibyte,
            instanceRoot: MissingVolumePath());

        AssertEx.True(verdict.Satisfied, verdict.Message);
        AssertEx.Equal(100 * Gibibyte, verdict.AvailableDiskBytes);
    }

    [Test]
    public async Task Evaluate_WithNoInstanceRoot_MeasuresTheNodeDataDirectory()
    {
        var verdict = await Evaluate(minimumMemoryMb: 512, availableRamBytes: 16 * Gibibyte, freeDiskBytes: 0, instanceRoot: string.Empty);

        AssertEx.True(verdict.AvailableDiskBytes > 0, "A measurable volume must report a positive figure.");
    }

    [Test]
    public async Task Evaluate_CarriesRequestedAgainstAvailableInBothDirections()
    {
        var verdict = await Evaluate(minimumMemoryMb: 1024, availableRamBytes: 2 * Gibibyte, freeDiskBytes: 50 * Gibibyte);

        AssertEx.Equal((1024L * 1024 * 1024) + ExternalAppResourceGate.MemoryHeadroomBytes, verdict.RequiredMemoryBytes);
        AssertEx.Equal(2 * Gibibyte, verdict.AvailableMemoryBytes);
        AssertEx.Equal(2 * Gibibyte, verdict.RequiredDiskBytes);
        AssertEx.NotEmpty(verdict.Message);
    }

    /// <summary>
    ///     The gate asks about the instance directory itself, not about its path root: on Linux every absolute path
    ///     roots at <c>/</c>, so a root-based question reports the root filesystem of a node whose data directory is
    ///     a mounted data volume. Resolving that path to a filesystem is the probe's job and is proved there.
    /// </summary>
    [Test]
    public async Task Evaluate_AsksTheProbeAboutTheInstanceRoot()
    {
        var probe = Substitute.For<IFreeSpaceProbe>();
        _ = probe.GetAvailableFreeBytes(Arg.Any<string>()).Returns(64 * Gibibyte);
        var instanceRoot = Path.Combine(Path.GetTempPath(), "xe-external-apps", Guid.NewGuid().ToString("N"), "volumes");

        var verdict = await Evaluate(minimumMemoryMb: 512,
            availableRamBytes: 16 * Gibibyte,
            freeDiskBytes: 1,
            instanceRoot: instanceRoot,
            freeSpace: probe);

        _ = probe.Received(requiredNumberOfCalls: 1).GetAvailableFreeBytes(instanceRoot);
        AssertEx.True(verdict.Satisfied, verdict.Message);
        AssertEx.Equal(64 * Gibibyte, verdict.AvailableDiskBytes);
    }

    /// <summary>A measurement that came back is the one that decides, even when it refuses the install.</summary>
    [Test]
    public async Task Evaluate_WithTooLittleDiskWhereTheInstanceWillLive_ReportsInsufficientDisk()
    {
        var probe = Substitute.For<IFreeSpaceProbe>();
        _ = probe.GetAvailableFreeBytes(Arg.Any<string>()).Returns(ExternalAppResourceGate.RequiredDiskBytes - 1);

        var verdict = await Evaluate(minimumMemoryMb: 512,
            availableRamBytes: 16 * Gibibyte,
            freeDiskBytes: 100 * Gibibyte,
            freeSpace: probe);

        AssertEx.False(verdict.Satisfied, "One byte short of the requirement is not enough.");
        AssertEx.Equal(ExternalAppFailureCategory.InsufficientDisk, verdict.FailureCategory);
        AssertEx.Equal(ExternalAppResourceGate.RequiredDiskBytes - 1, verdict.AvailableDiskBytes);
    }

    /// <summary>
    ///     A probe that cannot answer falls back to the hardware profile's figure rather than refusing the install:
    ///     "could not measure" must never reach a user as "your disk is full". Zero falls back as hard as a throw,
    ///     because a UNC path and a container bind answer nonsense rather than raising.
    /// </summary>
    [Test]
    public async Task Evaluate_WhenTheProbeCannotAnswer_FallsBackToTheProfile()
    {
        await AssertProfileFallback(static probe => probe.GetAvailableFreeBytes(Arg.Any<string>()).Returns(0L));

        // Every way the measurement refuses: a path string the framework will not resolve, no existing directory at
        // or above the path, a volume that is not ready, and a directory this process may not look at.
        await AssertProfileFallback(static probe => probe.GetAvailableFreeBytes(Arg.Any<string>()).Throws(new ArgumentException("not a path")));
        await AssertProfileFallback(static probe =>
            probe.GetAvailableFreeBytes(Arg.Any<string>()).Throws(new InvalidOperationException("nothing exists at or above the path")));
        await AssertProfileFallback(static probe => probe.GetAvailableFreeBytes(Arg.Any<string>()).Throws(new IOException("the volume is not ready")));
        await AssertProfileFallback(static probe => probe.GetAvailableFreeBytes(Arg.Any<string>()).Throws(new UnauthorizedAccessException("denied")));
    }

    private static async Task AssertProfileFallback(Action<IFreeSpaceProbe> arrange)
    {
        var probe = Substitute.For<IFreeSpaceProbe>();
        arrange(probe);

        var verdict = await Evaluate(minimumMemoryMb: 512, availableRamBytes: 16 * Gibibyte, freeDiskBytes: 100 * Gibibyte, freeSpace: probe);

        AssertEx.True(verdict.Satisfied, verdict.Message);
        AssertEx.Equal(100 * Gibibyte, verdict.AvailableDiskBytes);
    }

    private static async Task<ExternalAppResourceVerdict> Evaluate(int minimumMemoryMb,
        long availableRamBytes,
        long freeDiskBytes,
        string? instanceRoot = null,
        IFreeSpaceProbe? freeSpace = null)
    {
        var audit = Substitute.For<IRuntimeDeviceAudit>();
        audit.GetEffectiveProfileAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult(new HardwareProfile
             {
                 TotalRamBytes = availableRamBytes,
                 AvailableRamBytes = availableRamBytes,
                 VramBytes = 0,
                 AvailableVramBytes = null,
                 VramKnown = false,
                 GpuVendor = GpuVendor.None,
                 GpuAccelAvailable = false,
                 CpuCores = 4,
                 FreeDiskBytes = freeDiskBytes
             }));

        var gate = new ExternalAppResourceGate(audit, new FakeNodeDataDirectory(Path.GetTempPath()), freeSpace ?? new DriveInfoFreeSpaceProbe());
        var manifest = ExternalAppTestManifests.Manifest([ExternalAppTestManifests.Service("app")],
            resources: new ApplicationResources(minimumMemoryMb, minimumMemoryMb * 2, CpuHint: 1, PidsLimit: 512));

        // Unless a test says otherwise the volume is deliberately unmeasurable, so the disk figure is the profile's
        // and not whatever the machine running the suite happens to have free.
        return await gate.EvaluateAsync(manifest, instanceRoot ?? MissingVolumePath(), CancellationToken.None);
    }

    /// <summary>A path on a volume that does not exist, so the measurement fails the way an unmeasurable one does.</summary>
    private static string MissingVolumePath()
    {
        // Windows: a drive letter nothing is mounted on, which DriveInfo reports as an IOException. Unix: every
        // absolute path roots at "/", so the only way to make the measurement fail is a path the framework refuses.
        return OperatingSystem.IsWindows() ? @"Q:\xe-external-apps" : "\u0000not-a-path";
    }
}

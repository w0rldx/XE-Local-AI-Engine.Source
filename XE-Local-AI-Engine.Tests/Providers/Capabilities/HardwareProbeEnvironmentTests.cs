namespace XE_Local_AI_Engine.Tests.Providers.Capabilities;

using XE_Local_AI_Engine.Providers.Capabilities.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

/// <summary>
///     The live <see cref="HardwareProbeEnvironment" /> against the real OS, where <c>HardwareProfilerTests</c> fakes
///     the seam. Only the free-disk read is exercised here: it is the one value whose answer depends on WHICH
///     filesystem was asked, and the only one a fake cannot get wrong on the host's behalf.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class HardwareProbeEnvironmentTests
{
    /// <summary>
    ///     The models volume is operator-redirectable, so the figure must come from the mount holding it. It came
    ///     from the path's ROOT instead, which on Linux is <c>/</c> for every absolute path there is, so the
    ///     profiler reported the root filesystem's free space for a models directory on a mounted data volume.
    /// </summary>
    [Test]
    [RunOn(OS.Linux)]
    public void GetFreeDiskBytes_MeasuresTheMountHoldingThePathRatherThanTheRootFilesystem()
    {
        using var scratch = SeparateMountScratch.CreateOrSkip("xe-hardware-probe-disk");

        var measured = new HardwareProbeEnvironment().GetFreeDiskBytes(scratch.Path);

        AssertEx.Equal(scratch.ExpectedSatisfied,
            measured >= scratch.ThresholdBetweenBytes,
            scratch.WrongMountMessage + $" It answered {measured} bytes.");
    }

    /// <summary>
    ///     The profiler is handed the node data directory at registration time, before anything has created it on a
    ///     fresh node. A directory that does not exist yet must answer for the mount it will be created on — not 0,
    ///     which the model-fit page would show as a full disk.
    /// </summary>
    [Test]
    [RunOn(OS.Linux)]
    public void GetFreeDiskBytes_WhenThePathDoesNotExistYet_MeasuresTheMountOfItsNearestExistingAncestor()
    {
        using var scratch = SeparateMountScratch.CreateOrSkip("xe-hardware-probe-missing");
        var missing = Path.Combine(scratch.Path, "not", "created", "yet");

        var measured = new HardwareProbeEnvironment().GetFreeDiskBytes(missing);

        // 0 is the "cannot be resolved" answer and sits on the small-mount side of the threshold, so it is ruled out first.
        AssertEx.True(measured > 0, "A path that does not exist yet answered 0 instead of its ancestor's mount.");
        AssertEx.Equal(scratch.ExpectedSatisfied,
            measured >= scratch.ThresholdBetweenBytes,
            scratch.WrongMountMessage + $" It answered {measured} bytes.");
        AssertEx.False(Directory.Exists(missing), "Measuring free space must not create the directory.");
    }

    /// <summary>
    ///     The shared <c>IFreeSpaceProbe</c> refuses a path with no existing ancestor rather than answering a figure.
    ///     This probe's contract is the opposite — 0, never a throw — because the profiler behind it degrades every
    ///     failed read to its CPU-mode floor, and an exception out of here takes the whole hardware profile with it.
    /// </summary>
    [Test]
    [RunOn(OS.Windows)]
    public void GetFreeDiskBytes_WhenNothingExistsAtOrAboveThePath_AnswersZeroRatherThanThrowing()
    {
        // Unix has no such path: every absolute path walks up to "/", which always exists. Q: is assumed to be
        // unmounted on the runner, the same assumption DriveInfoFreeSpaceProbeTests makes for the throwing side.
        AssertEx.Equal(0L,
            new HardwareProbeEnvironment().GetFreeDiskBytes(@"Q:\xe-hardware-probe-disk"),
            "An unmeasurable path must read 0 here; anything else means the probe's refusal escaped this catch.");
    }
}

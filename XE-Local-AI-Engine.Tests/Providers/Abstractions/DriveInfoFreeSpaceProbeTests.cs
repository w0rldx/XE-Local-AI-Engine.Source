namespace XE_Local_AI_Engine.Tests.Providers.Abstractions;

using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

/// <summary>
///     The one measurement every disk guard in the node goes through: the models directory, a GGUF import, the
///     training base artifacts, the KLD benchmark cache and the External Apps admission gate. What it measures is
///     the filesystem holding the closest existing ancestor of the path, never the path's root.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class DriveInfoFreeSpaceProbeTests
{
    [Test]
    public void GetAvailableFreeBytes_ForADirectoryThatDoesNotExistYet_MeasuresItsClosestExistingAncestor()
    {
        var probe = new DriveInfoFreeSpaceProbe();
        var ancestor = Directory.CreateTempSubdirectory("xe-free-space-probe");
        try
        {
            var missing = Path.Combine(ancestor.FullName, "not-created", "yet", Guid.NewGuid().ToString("N"));

            // One measurement for both assertions: free space moves under a parallel test run, so two calls are two
            // different figures and the pair would no longer describe a single answer.
            var measured = probe.GetAvailableFreeBytes(missing);

            // Both figures come from the same filesystem, so this says WHICH directory was measured rather than
            // pinning a number: the total size is what a wrong filesystem would blow past.
            AssertEx.True(measured <= new DriveInfo(ancestor.FullName).TotalSize,
                "A path measured on a different filesystem can report more free space than this one holds in total.");
            AssertEx.True(measured > 0, "An existing ancestor must produce a real measurement.");
        }
        finally
        {
            ancestor.Delete(recursive: true);
        }
    }

    /// <summary>
    ///     The regression itself. On Linux <c>Path.GetPathRoot</c> answers <c>/</c> for every absolute path there is,
    ///     so a root-based measurement reported the root filesystem for a directory on a mounted data volume.
    /// </summary>
    [Test]
    [RunOn(OS.Linux)]
    public void GetAvailableFreeBytes_DoesNotReportTheRootFilesystemForAPathOnAnotherVolume()
    {
        var temporary = new DriveInfo(Path.GetTempPath());
        var rootFilesystem = new DriveInfo("/");

        if (rootFilesystem.AvailableFreeSpace <= temporary.TotalSize)
        {
            Skip.Test($"The temporary directory '{Path.GetTempPath()}' and '/' cannot be told apart on this host: "
                      + "the root filesystem's free space does not exceed the temporary filesystem's total size, so a "
                      + "root-based measurement would be indistinguishable from a correct one.");
        }

        var missing = Path.Combine(Path.GetTempPath(), "xe-free-space-probe", Guid.NewGuid().ToString("N"));

        AssertEx.True(new DriveInfoFreeSpaceProbe().GetAvailableFreeBytes(missing) <= temporary.TotalSize,
            "The measurement came from the root filesystem rather than from the one holding the temporary directory.");
    }

    /// <summary>A path with no existing ancestor is unmeasurable, and says so rather than answering with a figure.</summary>
    [Test]
    [RunOn(OS.Windows)]
    public void GetAvailableFreeBytes_WhenNothingExistsAtOrAboveThePath_Throws()
    {
        // Unix has no such path: every absolute path walks up to "/", which always exists. Q: is assumed to be
        // unmounted on the runner; a box that does mount it fails this loudly rather than passing wrongly, because
        // the probe would then answer with a figure where the test demands a refusal.
        _ = AssertEx.Throws<InvalidOperationException>(() => new DriveInfoFreeSpaceProbe().GetAvailableFreeBytes(@"Q:\xe-free-space-probe"));
    }

    [Test]
    public void GetAvailableFreeBytes_WithNoPath_Throws()
    {
        _ = AssertEx.Throws<ArgumentException>(() => new DriveInfoFreeSpaceProbe().GetAvailableFreeBytes("  "));
    }
}

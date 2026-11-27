namespace XE_Local_AI_Engine.Tests.Providers.Abstractions;

using System.Runtime.InteropServices;
using XE_Local_AI_Engine.Client.Services.Knowledge.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.HuggingFace.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

/// <summary>
///     The architecture-specific open flags against the kernel's own tables (asm-generic for x86-64, <c>arch/arm64</c> uapi
///     <c>fcntl.h</c> for arm64), each no-follow open's composed flags, and the flags against the running kernel.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class LinuxOpenFlagsTests
{
    private const int Eloop = 40;
    private const int Enotdir = 20;

    [Test]
    [Arguments(Architecture.X64, 0x20000, 0x10000)]
    [Arguments(Architecture.Arm64, 0x8000, 0x4000)]
    [Arguments(Architecture.Arm, 0x8000, 0x4000)]
    public void NoFollowAndDirectory_MatchTheKernelTable(Architecture architecture, int noFollow, int directory)
    {
        AssertEx.Equal(noFollow, LinuxOpenFlags.NoFollowFor(architecture));
        AssertEx.Equal(directory, LinuxOpenFlags.DirectoryFor(architecture));
    }

    [Test]
    public void TheProcessValues_AreThisArchitecturesRow()
    {
        AssertEx.Equal(LinuxOpenFlags.NoFollowFor(RuntimeInformation.ProcessArchitecture), LinuxOpenFlags.NoFollow);
        AssertEx.Equal(LinuxOpenFlags.DirectoryFor(RuntimeInformation.ProcessArchitecture), LinuxOpenFlags.Directory);
    }

    /// <summary>Each open keeps the asm-generic bits it always set (read/write mode, create, truncate, non-blocking, close-on-exec).</summary>
    [Test]
    public void EveryNoFollowOpen_KeepsItsFlagSet()
    {
        const int closeOnExec = 0x80000;
        const int writeCreateTruncate = 0x1 | 0x40 | 0x200;
        var noFollow = LinuxOpenFlags.NoFollow;

        AssertEx.Equal(noFollow | closeOnExec, KnowledgeRepositoryImportService.ReadOnlyNoFollowCloseOnExecFlags);
        AssertEx.Equal(0x800 | noFollow | closeOnExec, ValidatedGgufImportSource.LinuxReadOnlyNoFollowNonBlockingCloseOnExec);
        AssertEx.Equal(noFollow | closeOnExec, SandboxJailPathGuard.ReadOnlyNoFollowCloseOnExecFlags);
        AssertEx.Equal(writeCreateTruncate | noFollow | closeOnExec, SandboxJailPathGuard.WriteCreateNoFollowCloseOnExecFlags);
        AssertEx.Equal(writeCreateTruncate | noFollow | closeOnExec, DockerWorkspaceHostFiles.WriteCreateNoFollowCloseOnExecFlags);
    }

    [Test]
    [RunOn(OS.Linux)]
    public void Open_WithNoFollowOnASymlink_FailsWithEloop()
    {
        using var directory = new TempDirectory("xe-open-flags");
        var target = directory.FilePath("target.txt");
        File.WriteAllText(target, "target");
        var link = directory.FilePath("link.txt");
        File.CreateSymbolicLink(link, target);

        var descriptor = LinuxOpenFlags.Open(link, LinuxOpenFlags.ReadOnly | LinuxOpenFlags.NoFollow | LinuxOpenFlags.CloseOnExec);
        var errno = Marshal.GetLastPInvokeError();

        AssertEx.Equal(-1, descriptor, "O_NOFOLLOW must refuse a symlink leaf on this kernel.");
        AssertEx.Equal(Eloop, errno);
    }

    [Test]
    [RunOn(OS.Linux)]
    public void Open_WithDirectoryOnARegularFile_FailsWithEnotdir()
    {
        using var directory = new TempDirectory("xe-open-flags");
        var file = directory.FilePath("file.txt");
        File.WriteAllText(file, "file");

        var descriptor = LinuxOpenFlags.Open(file, LinuxOpenFlags.PathOnly | LinuxOpenFlags.Directory | LinuxOpenFlags.CloseOnExec);
        var errno = Marshal.GetLastPInvokeError();

        AssertEx.Equal(-1, descriptor, "O_DIRECTORY must refuse a regular file on this kernel.");
        AssertEx.Equal(Enotdir, errno);
    }
}

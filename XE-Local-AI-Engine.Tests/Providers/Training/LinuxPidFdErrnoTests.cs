namespace XE_Local_AI_Engine.Tests.Providers.Training;

using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>What each errno of a failed pidfd call means: only a filter's refusal of the open reads unsupported, and nothing operational proves a process gone.</summary>
[Category(TestCategories.Unit)]
public sealed class LinuxPidFdErrnoTests
{
    /// <summary><c>pidfd_open</c> checks no permission on its target, so EPERM and EACCES can only be a filter refusing the call: the host has no usable pidfd.</summary>
    [Test]
    [Arguments(3, LinuxPidFdOpenStatus.Gone)]
    [Arguments(38, LinuxPidFdOpenStatus.Unsupported)]
    [Arguments(1, LinuxPidFdOpenStatus.Unsupported)]
    [Arguments(13, LinuxPidFdOpenStatus.Unsupported)]
    [Arguments(24, LinuxPidFdOpenStatus.Failed)]
    [Arguments(23, LinuxPidFdOpenStatus.Failed)]
    [Arguments(12, LinuxPidFdOpenStatus.Failed)]
    public void AFailedOpen_MapsItsErrno(int errno, LinuxPidFdOpenStatus expected)
    {
        AssertEx.Equal(expected, LinuxPidFd.OpenStatusOf(errno), "EMFILE, ENFILE and ENOMEM stay fail-closed.");
    }

    /// <summary>A refused send is a real failure: EPERM there is a permission check on the target, never a missing feature.</summary>
    [Test]
    [Arguments(3, false, LinuxPidFdSendStatus.Gone)]
    [Arguments(22, true, LinuxPidFdSendStatus.GroupFlagUnsupported)]
    [Arguments(22, false, LinuxPidFdSendStatus.Failed)]
    [Arguments(1, false, LinuxPidFdSendStatus.Failed)]
    [Arguments(1, true, LinuxPidFdSendStatus.Failed)]
    public void AFailedSend_MapsItsErrno(int errno, bool group, LinuxPidFdSendStatus expected)
    {
        AssertEx.Equal(expected, LinuxPidFd.SendStatusOf(errno, group));
    }
}

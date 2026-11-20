namespace XE_Local_AI_Engine.Providers.Abstractions;

using System.Runtime.InteropServices;

/// <summary>What <see cref="LinuxPidFd.Open" /> found.</summary>
public enum LinuxPidFdOpenStatus
{
    /// <summary>The fd is open and names the process that held the pid at the open.</summary>
    Opened,

    /// <summary>ESRCH: no process holds the pid.</summary>
    Gone,

    /// <summary>
    ///     ENOSYS, a platform other than Linux on x64 or arm64, or EPERM or EACCES: <c>pidfd_open</c> checks no permission on its target, so
    ///     either can only come from a seccomp, systemd or LSM filter refusing the call itself.
    /// </summary>
    Unsupported,

    /// <summary>Any other errno (EMFILE, ENFILE, ENOMEM, …): an operational failure, never proof of anything.</summary>
    Failed
}

/// <summary>What <see cref="LinuxPidFd.Send" /> did.</summary>
public enum LinuxPidFdSendStatus
{
    /// <summary>The kernel accepted the signal.</summary>
    Sent,

    /// <summary>ESRCH: the process has exited and been reaped, or for a group send no member is left; also a closed or never-opened fd.</summary>
    Gone,

    /// <summary>EINVAL on a group send: a kernel before 6.9, or a filter refusing the flag.</summary>
    GroupFlagUnsupported,

    /// <summary>Any other errno: an operational failure.</summary>
    Failed
}

/// <summary>A pidfd: a reference to one process that a later pid reuse cannot redirect, so a signal sent through it reaches that process or nothing.</summary>
/// <remarks>
///     glibc wraps <c>pidfd_open</c> and <c>pidfd_send_signal</c> only from 2.36, so both go through <c>syscall(2)</c>; the numbers are the unified
///     asm-generic ones x64 and arm64 share. The kernel sets <c>O_CLOEXEC</c> on the fd, so no later spawn inherits it. fd 0 is valid; only -1 is not.
/// </remarks>
public sealed partial class LinuxPidFd : SafeHandle
{
    private const nint PidFdOpenSyscall = 434;
    private const nint PidFdSendSignalSyscall = 424;

    // PIDFD_SIGNAL_PROCESS_GROUP in linux/pidfd.h (Linux 6.9): signal the process group the pid names.
    private const nint SignalProcessGroup = 1 << 2;
    private const int Eperm = 1;
    private const int Esrch = 3;
    private const int Eacces = 13;
    private const int Einval = 22;
    private const int Enosys = 38;

    /// <summary>Wraps <paramref name="preexistingHandle" />, closed on dispose only when <paramref name="ownsHandle" />, and reports it as <paramref name="openStatus" />.</summary>
    /// <remarks>Internal: only <see cref="Open" /> and test fakes may claim that a number is a pidfd.</remarks>
    internal LinuxPidFd(nint preexistingHandle, bool ownsHandle, LinuxPidFdOpenStatus openStatus)
        : base(invalidHandleValue: -1, ownsHandle)
    {
        SetHandle(preexistingHandle);
        OpenStatus = openStatus;
    }

    public LinuxPidFdOpenStatus OpenStatus { get; private set; }

    /// <summary>The errno <see cref="Open" /> failed with; 0 once opened. Set by the open alone, so no later send can change it.</summary>
    public int OpenErrno { get; private set; }

    public override bool IsInvalid => handle == -1;

    /// <summary>A handle that holds no fd and reads <see cref="LinuxPidFdOpenStatus.Unsupported" />, for a caller that must not reach the kernel.</summary>
    public static LinuxPidFd Unsupported() =>
        new(preexistingHandle: -1, ownsHandle: false, LinuxPidFdOpenStatus.Unsupported);

    /// <summary>Opens a pidfd on <paramref name="pid" />. Never throws; the caller disposes the result whatever its <see cref="OpenStatus" />.</summary>
    public static LinuxPidFd Open(int pid)
    {
        // Allocated before the call, so nothing can fail between the kernel handing out the fd and the handle owning it.
        var pidFd = new LinuxPidFd(preexistingHandle: -1, ownsHandle: true, LinuxPidFdOpenStatus.Unsupported);
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
        {
            return pidFd;
        }

        var fd = PidFdOpen(PidFdOpenSyscall, pid, flags: 0);
        if (fd >= 0)
        {
            pidFd.SetHandle(fd);
            pidFd.OpenStatus = LinuxPidFdOpenStatus.Opened;
            return pidFd;
        }

        pidFd.OpenErrno = Marshal.GetLastPInvokeError();
        pidFd.OpenStatus = OpenStatusOf(pidFd.OpenErrno);
        return pidFd;
    }

    /// <summary>What a failed <c>pidfd_open</c> means, by its errno.</summary>
    internal static LinuxPidFdOpenStatus OpenStatusOf(int errno) =>
        errno switch
        {
            Esrch => LinuxPidFdOpenStatus.Gone,
            Enosys or Eperm or Eacces => LinuxPidFdOpenStatus.Unsupported,
            _ => LinuxPidFdOpenStatus.Failed
        };

    /// <summary>What a failed <c>pidfd_send_signal</c> means, by its errno.</summary>
    internal static LinuxPidFdSendStatus SendStatusOf(int errno, bool group) =>
        errno switch
        {
            Esrch => LinuxPidFdSendStatus.Gone,
            Einval when group => LinuxPidFdSendStatus.GroupFlagUnsupported,
            _ => LinuxPidFdSendStatus.Failed
        };

    /// <summary>Sends <paramref name="signal" /> (0 probes) to the process, or with <paramref name="group" /> to the process group its pid names. Never throws.</summary>
    public LinuxPidFdSendResult Send(int signal, bool group)
    {
        if (IsInvalid || IsClosed)
        {
            return new LinuxPidFdSendResult(LinuxPidFdSendStatus.Gone, Errno: 0);
        }

        nint result;
        try
        {
            // The marshaller holds a reference on the handle across the call, so a concurrent dispose cannot close the fd under it.
            result = PidFdSendSignal(PidFdSendSignalSyscall, this, signal, info: 0, group ? SignalProcessGroup : 0);
        }
        catch (ObjectDisposedException)
        {
            return new LinuxPidFdSendResult(LinuxPidFdSendStatus.Gone, Errno: 0);
        }

        if (result == 0)
        {
            return new LinuxPidFdSendResult(LinuxPidFdSendStatus.Sent, Errno: 0);
        }

        var errno = Marshal.GetLastPInvokeError();
        return new LinuxPidFdSendResult(SendStatusOf(errno, group), errno);
    }

    protected override bool ReleaseHandle() =>
        Close((int)handle) == 0;

    // long syscall(long number, ...) for int pidfd_open(pid_t pid, unsigned int flags); every argument native-width, as the stub reads registers.
    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial nint PidFdOpen(nint number, nint pid, nint flags);

    // long syscall(long number, ...) for int pidfd_send_signal(int pidfd, int sig, siginfo_t *info, unsigned int flags).
    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial nint PidFdSendSignal(nint number, LinuxPidFd pidFd, nint signal, nint info, nint flags);

    // close(2): releases the fd once the last reference to the handle is gone.
    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int Close(int fd);
}

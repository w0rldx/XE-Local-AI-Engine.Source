namespace XE_Local_AI_Engine.Providers.ProcessSupervision;

using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

/// <summary>The real <see cref="IWindowsJobApi" />: kernel32, nothing else.</summary>
/// <remarks>
///     Handle rules (ADR 0025): every pipe is created non-inheritable. The three child ends become inheritable only inside
///     <see cref="LaunchLock" />, around the one <c>CreateProcessW</c> whose <c>PROC_THREAD_ATTRIBUTE_HANDLE_LIST</c> names exactly them, and
///     the engine's copies are closed right after. The parent ends are never inheritable; stdout and stderr are read overlapped. .NET's
///     own <c>Process.Start</c> inherits every inheritable handle under a lock no other code can take, so a concurrent start inside that
///     window can still inherit a child end; the cost is a delayed end of output, which the reader's cancellable drain budget bounds.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class WindowsJobApi : IWindowsJobApi
{
    private const uint PipeBufferBytes = 65_536;

    private static readonly Lock LaunchLock = new();

    public int GetSystemProcessorCount() =>
        (int)WindowsJobNative.GetActiveProcessorCount(WindowsJobNative.AllProcessorGroups);

    public SafeHandle CreateJob()
    {
        var job = WindowsJobNative.CreateJobObjectW(IntPtr.Zero, lpName: null);
        if (job.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            job.Dispose();
            throw new Win32Exception(error);
        }

        return job;
    }

    public void SetExtendedLimits(SafeHandle job, WindowsJobLimits limits)
    {
        WindowsJobNative.SetInformation(job,
            WindowsJobNative.JobObjectExtendedLimitInformation,
            new WindowsJobNative.JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new WindowsJobNative.JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = limits.LimitFlags,
                    ActiveProcessLimit = limits.ActiveProcessLimit
                },
                JobMemoryLimit = checked((nuint)limits.JobMemoryLimit)
            });
    }

    public void SetCpuRate(SafeHandle job, uint cpuRate)
    {
        WindowsJobNative.SetInformation(job,
            WindowsJobNative.JobObjectCpuRateControlInformation,
            new WindowsJobNative.JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
            {
                ControlFlags = WindowsJobNative.CpuRateControlEnable | WindowsJobNative.CpuRateControlHardCap,
                CpuRate = cpuRate
            });
    }

    public WindowsJobLimits QueryLimits(SafeHandle job)
    {
        var extended = WindowsJobNative.QueryInformation<WindowsJobNative.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>(job,
            WindowsJobNative.JobObjectExtendedLimitInformation);
        var cpu = WindowsJobNative.QueryInformation<WindowsJobNative.JOBOBJECT_CPU_RATE_CONTROL_INFORMATION>(job,
            WindowsJobNative.JobObjectCpuRateControlInformation);
        var rate = cpu.ControlFlags switch
        {
            0 => 0u,
            WindowsJobNative.CpuRateControlEnable | WindowsJobNative.CpuRateControlHardCap => cpu.CpuRate,
            _ => throw new InvalidOperationException($"the Windows job reads back CPU rate control flags 0x{cpu.ControlFlags:X}, not a plain hard cap")
        };

        return new WindowsJobLimits
        {
            LimitFlags = extended.BasicLimitInformation.LimitFlags,
            JobMemoryLimit = extended.JobMemoryLimit,
            ActiveProcessLimit = extended.BasicLimitInformation.ActiveProcessLimit,
            CpuRate = rate
        };
    }

    [SuppressMessage("Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The parent ends and the process and thread handles are owned by the returned child; every failure path disposes all of them.")]
    public WindowsJobChild CreateSuspended(SafeHandle job, WindowsJobStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        SafeFileHandle? stdinRead = null, stdinWrite = null, stdoutRead = null, stdoutWrite = null, stderrRead = null, stderrWrite = null;
        SafeProcessHandle? process = null;
        SafeWaitHandle? thread = null;
        try
        {
            CreatePipe(out stdinRead, out stdinWrite);
            CreateOverlappedReadPipe(out stdoutRead, out stdoutWrite);
            CreateOverlappedReadPipe(out stderrRead, out stderrWrite);

            uint processId;
            lock (LaunchLock)
            {
                try
                {
                    MakeInheritable(stdinRead);
                    MakeInheritable(stdoutWrite);
                    MakeInheritable(stderrWrite);
                    var created = CreateProcess(job, startInfo, stdinRead, stdoutWrite, stderrWrite);
                    // Owned at once, before anything else here can throw.
                    process = new SafeProcessHandle(created.hProcess, ownsHandle: true);
                    thread = new SafeWaitHandle(created.hThread, ownsHandle: true);
                    processId = created.dwProcessId;
                }
                finally
                {
                    // The child holds its own copies now; the engine's must not outlive this window.
                    stdinRead.Dispose();
                    stdoutWrite.Dispose();
                    stderrWrite.Dispose();
                }
            }

            return new WindowsJobChild
            {
                Process = process,
                Thread = thread,
                ProcessId = checked((int)processId),
                StandardInput = new FileStream(stdinWrite, FileAccess.Write, bufferSize: 4096, isAsync: false),
                StandardOutput = new FileStream(stdoutRead, FileAccess.Read, bufferSize: 4096, isAsync: true),
                StandardError = new FileStream(stderrRead, FileAccess.Read, bufferSize: 4096, isAsync: true)
            };
        }
        catch
        {
            thread?.Dispose();
            process?.Dispose();
            stdinRead?.Dispose();
            stdinWrite?.Dispose();
            stdoutRead?.Dispose();
            stdoutWrite?.Dispose();
            stderrRead?.Dispose();
            stderrWrite?.Dispose();
            throw;
        }
    }

    public bool IsProcessInJob(SafeHandle process, SafeHandle job)
    {
        if (!WindowsJobNative.IsProcessInJob(process, job, out var member))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        return member;
    }

    public void Resume(SafeHandle thread)
    {
        if (WindowsJobNative.ResumeThread(thread) == uint.MaxValue)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    public void Terminate(SafeHandle job)
    {
        if (!WindowsJobNative.TerminateJobObject(job, uExitCode: 1))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    [SuppressMessage("Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The wait handle is disposed by the continuation that unregisters it, once the process has exited.")]
    public Task WaitForExitAsync(SafeHandle process)
    {
        // A duplicate the registration owns: the caller may close the process handle while the wait is still registered.
        if (!WindowsJobNative.DuplicateHandle(WindowsJobNative.GetCurrentProcess(),
                process,
                WindowsJobNative.GetCurrentProcess(),
                out var duplicate,
                WindowsJobNative.Synchronize,
                bInheritHandle: false,
                dwOptions: 0))
        {
            var error = Marshal.GetLastPInvokeError();
            duplicate.Dispose();
            throw new Win32Exception(error);
        }

        var waitHandle = new ProcessExitWaitHandle(duplicate);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = ThreadPool.RegisterWaitForSingleObject(waitHandle,
            static (state, _) => ((TaskCompletionSource)state!).TrySetResult(),
            exited,
            Timeout.Infinite,
            executeOnlyOnce: true);
        return exited.Task.ContinueWith(_ =>
            {
                registration.Unregister(waitObject: null);
                waitHandle.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public int GetExitCode(SafeHandle process)
    {
        if (!WindowsJobNative.GetExitCodeProcess(process, out var exitCode))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        return unchecked((int)exitCode);
    }

    public void TerminateProcess(SafeHandle process)
    {
        if (!WindowsJobNative.TerminateProcess(process, uExitCode: 1))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    public bool TryCommit(SafeHandle process, ulong bytes, out int error)
    {
        var address = WindowsJobNative.VirtualAllocEx(process,
            IntPtr.Zero,
            checked((nuint)bytes),
            WindowsJobNative.MemCommit | WindowsJobNative.MemReserve,
            WindowsJobNative.PageReadWrite);
        if (address == IntPtr.Zero)
        {
            error = Marshal.GetLastPInvokeError();
            return false;
        }

        error = 0;
        _ = WindowsJobNative.VirtualFreeEx(process, address, 0, WindowsJobNative.MemRelease);
        return true;
    }

    private static void CreatePipe(out SafeFileHandle read, out SafeFileHandle write)
    {
        if (!WindowsJobNative.CreatePipe(out read, out write, IntPtr.Zero, 0))
        {
            var error = Marshal.GetLastPInvokeError();
            read.Dispose();
            write.Dispose();
            throw new Win32Exception(error);
        }
    }

    /// <summary>An overlapped server end the engine reads and a synchronous client end the child writes.</summary>
    /// <remarks>
    ///     Windows documents cancelling a blocked read on another thread only for overlapped I/O, so the drain budget needs this read end
    ///     overlapped. One instance, local clients only, a GUID name: the engine's own client open is the pipe's only connection.
    /// </remarks>
    private static void CreateOverlappedReadPipe(out SafeFileHandle read, out SafeFileHandle write)
    {
        var name = string.Create(CultureInfo.InvariantCulture, $@"\\.\pipe\xe-sandbox-{Environment.ProcessId}-{Guid.NewGuid():N}");
        read = WindowsJobNative.CreateNamedPipeW(name,
            WindowsJobNative.PipeAccessInbound | WindowsJobNative.FileFlagOverlapped | WindowsJobNative.FileFlagFirstPipeInstance,
            WindowsJobNative.PipeRejectRemoteClients,
            nMaxInstances: 1,
            nOutBufferSize: PipeBufferBytes,
            nInBufferSize: PipeBufferBytes,
            nDefaultTimeOut: 0,
            IntPtr.Zero);
        if (read.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            read.Dispose();
            write = new SafeFileHandle();
            throw new Win32Exception(error);
        }

        write = WindowsJobNative.CreateFileW(name, WindowsJobNative.GenericWrite, 0, IntPtr.Zero, WindowsJobNative.OpenExisting, 0, IntPtr.Zero);
        if (write.IsInvalid || (!WindowsJobNative.ConnectNamedPipe(read, IntPtr.Zero) && Marshal.GetLastPInvokeError() != WindowsJobNative.ErrorPipeConnected))
        {
            var error = Marshal.GetLastPInvokeError();
            read.Dispose();
            write.Dispose();
            throw new Win32Exception(error);
        }
    }

    private static void MakeInheritable(SafeHandle handle)
    {
        if (!WindowsJobNative.SetHandleInformation(handle, WindowsJobNative.HandleFlagInherit, WindowsJobNative.HandleFlagInherit))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    [SuppressMessage("Sonar Code Smell",
        "S3869:SafeHandle instances should not use DangerousGetHandle",
        Justification = "PROC_THREAD_ATTRIBUTE_JOB_LIST, PROC_THREAD_ATTRIBUTE_HANDLE_LIST and STARTUPINFO take raw handle values; each SafeHandle is AddRef'd for the whole call and released only after the attribute list is deleted.")]
    private static WindowsJobNative.PROCESS_INFORMATION CreateProcess(SafeHandle job,
        WindowsJobStartInfo startInfo,
        SafeHandle stdin,
        SafeHandle stdout,
        SafeHandle stderr)
    {
        var commandLine = (startInfo.CommandLine + '\0').ToCharArray();
        var environment = BuildEnvironmentBlock(startInfo.Environment);
        var workingDirectory = string.IsNullOrEmpty(startInfo.WorkingDirectory) ? null : startInfo.WorkingDirectory;

        nint size = 0;
        _ = WindowsJobNative.InitializeProcThreadAttributeList(IntPtr.Zero, dwAttributeCount: 2, dwFlags: 0, ref size);
        // Allocated inside the try, so a failed allocation frees the earlier ones; FreeHGlobal of IntPtr.Zero does nothing.
        IntPtr attributes = IntPtr.Zero, jobs = IntPtr.Zero, inherited = IntPtr.Zero;
        var initialized = false;
        bool jobRef = false, stdinRef = false, stdoutRef = false, stderrRef = false;
        try
        {
            attributes = Marshal.AllocHGlobal(size);
            // Both arrays must outlive the attribute list, which stores their addresses.
            jobs = Marshal.AllocHGlobal(IntPtr.Size);
            inherited = Marshal.AllocHGlobal(3 * IntPtr.Size);
            job.DangerousAddRef(ref jobRef);
            stdin.DangerousAddRef(ref stdinRef);
            stdout.DangerousAddRef(ref stdoutRef);
            stderr.DangerousAddRef(ref stderrRef);
            if (!WindowsJobNative.InitializeProcThreadAttributeList(attributes, dwAttributeCount: 2, dwFlags: 0, ref size))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            initialized = true;
            Marshal.WriteIntPtr(jobs, job.DangerousGetHandle());
            Marshal.WriteIntPtr(inherited, 0, stdin.DangerousGetHandle());
            Marshal.WriteIntPtr(inherited, IntPtr.Size, stdout.DangerousGetHandle());
            Marshal.WriteIntPtr(inherited, 2 * IntPtr.Size, stderr.DangerousGetHandle());
            if (!WindowsJobNative.UpdateProcThreadAttribute(attributes, 0, WindowsJobNative.ProcThreadAttributeJobList, jobs, IntPtr.Size, IntPtr.Zero, IntPtr.Zero)
                || !WindowsJobNative.UpdateProcThreadAttribute(attributes,
                    0,
                    WindowsJobNative.ProcThreadAttributeHandleList,
                    inherited,
                    3 * IntPtr.Size,
                    IntPtr.Zero,
                    IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            var startup = new WindowsJobNative.STARTUPINFOEXW
            {
                StartupInfo = new WindowsJobNative.STARTUPINFOW
                {
                    cb = (uint)Marshal.SizeOf<WindowsJobNative.STARTUPINFOEXW>(),
                    dwFlags = WindowsJobNative.StartfUseStdHandles,
                    hStdInput = stdin.DangerousGetHandle(),
                    hStdOutput = stdout.DangerousGetHandle(),
                    hStdError = stderr.DangerousGetHandle()
                },
                lpAttributeList = attributes
            };

            // Never a UI-limited job and never a breakaway flag: the job's own flags are the whole story.
            const uint flags = WindowsJobNative.CreateSuspended
                               | WindowsJobNative.CreateUnicodeEnvironment
                               | WindowsJobNative.ExtendedStartupInfoPresent
                               | WindowsJobNative.CreateNoWindow;
            if (!WindowsJobNative.CreateProcessW(lpApplicationName: null,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    bInheritHandles: true,
                    flags,
                    environment,
                    workingDirectory,
                    ref startup,
                    out var created))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            return created;
        }
        finally
        {
            if (initialized)
            {
                WindowsJobNative.DeleteProcThreadAttributeList(attributes);
            }

            Marshal.FreeHGlobal(inherited);
            Marshal.FreeHGlobal(jobs);
            Marshal.FreeHGlobal(attributes);
            ReleaseIf(stderr, stderrRef);
            ReleaseIf(stdout, stdoutRef);
            ReleaseIf(stdin, stdinRef);
            ReleaseIf(job, jobRef);
        }
    }

    private static void ReleaseIf(SafeHandle handle, bool added)
    {
        if (added)
        {
            handle.DangerousRelease();
        }
    }

    // NAME=value\0 ... \0, sorted case-insensitively as Windows and .NET's Process sort the block.
    private static char[] BuildEnvironmentBlock(IReadOnlyDictionary<string, string> environment)
    {
        var builder = new StringBuilder();
        foreach (var name in environment.Keys.Order(StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(name).Append('=').Append(environment[name]).Append('\0');
        }

        if (builder.Length == 0)
        {
            builder.Append('\0');
        }

        return builder.Append('\0').ToString().ToCharArray();
    }

    private sealed class ProcessExitWaitHandle : WaitHandle
    {
        public ProcessExitWaitHandle(SafeWaitHandle handle)
        {
            SafeWaitHandle = handle;
        }
    }
}
